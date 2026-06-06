# MqttClientService — silent inbound-processing death (focus: drain loop, handler exception safety, channel correctness)

## Conclusion (one line)
The handler is correctly defused; the real exposure is the **fire-and-forget, unsupervised `DrainMessageChannelAsync`** — if its `await foreach` ever throws *outside* the inner per-message `try/catch`, the drain Task faults silently, the channel Reader is never consumed again, `OnMessageReceivedAsync` keeps `TryWrite`-ing into an unbounded channel nobody drains, and **publishes keep working**. That is an exact match for the reported symptom.

## Background fact (web-verified)
MQTTnet 5's receive loop is **serialized**: it awaits the Task returned by `ApplicationMessageReceivedAsync` before reading the next packet, and with default `AutoAcknowledge=true` it sends the QoS1 PUBACK when that Task completes.
- dotnet/MQTTnet issue #829 — "When client is receiving long processing topics, it will disconnect" (keepalive starved while handler blocks).
- dotnet/MQTTnet issue #1646 — "Freeze on long running async operation in ApplicationMessageReceivedAsync" (loop blocked until handler returns).
- dotnet/MQTTnet discussion #1589 — maintainer recommends exactly the Channel offload pattern this code uses.
- dotnet/MQTTnet discussion #1468 + Samples/Client/Client_Subscribe_Samples.cs (`ConcurrentProcessingDisableAutoAcknowledge`) — default AutoAcknowledge acks QoS1 on handler return.

Because `OnMessageReceivedAsync` returns `Task.CompletedTask` immediately after `TryWrite`, it does NOT block the loop or starve keepalive, and the QoS1 ack fires immediately. So the handler is NOT the stall point. The stall is downstream.

---

## Finding 1 — HIGH: drain task is fire-and-forget and unsupervised
`MqttClientService.cs:81` `_ = Task.Run(() => DrainMessageChannelAsync(stoppingToken), stoppingToken);`
- Discarded with `_ =`; no await, no `ContinueWith` fault observer, no `TaskScheduler.UnobservedTaskException` handler.
- `DrainMessageChannelAsync` (lines 219–226): the `try/catch` is **inside** the `await foreach` body (wraps only `ProcessMessageAsync`). A throw from the `ReadAllAsync`/enumerator machinery is NOT caught → method returns faulted → task dropped.
- Single-reader unbounded channel (lines 34–35) is drained only by this one task. Once dead, `OnMessageReceivedAsync` (line 210) keeps enqueueing forever; nothing renders. `PublishAsync` (line 344) is a separate path → still works.
- Same fire-and-forget risk: `ConnectWithRetryAsync` (line 80), `ButtonPressed` handler (line 78).

**Fix:** outer `try/catch` around the `await foreach`; catch `OperationCanceledException` on shutdown and return cleanly, otherwise `LogCritical` and restart the drainer. Add a global `UnobservedTaskException` logger. Prefer a dedicated awaited `BackgroundService` loop.

## Finding 2 — MEDIUM (b/d): `ReadAllAsync(ct)` cancellation is the loop's only exit, and it is unhandled
- `ReadAllAsync(ct)` (line 221) returns normally **only** if `Channel.Writer.Complete()` is called. `Complete()` is **never** called anywhere in the file (Writer is used only via `TryWrite`, line 210).
- Therefore the loop's only termination is the token cancelling, which makes `ReadAllAsync` throw `OperationCanceledException` — unhandled in `DrainMessageChannelAsync`, faulting the fire-and-forget task from Finding 1.
- (d) `Task.Run(fn, stoppingToken)` second arg only gates whether the delegate starts. Once running, cancellation is driven solely by the token threaded into `ReadAllAsync` → no harmful double-cancel, but the cancel manifests as a thrown OCE, not a clean stop.

**Fix:** explicitly catch `OperationCanceledException` when `ct.IsCancellationRequested` and return; catch everything else, log Critical, restart.

## Finding 3 — MEDIUM (g): the failure is invisible in logs
- `OnMessageReceivedAsync` logs at **Debug** (line 209) *before* the channel write; with Debug off (prod/Docker) there is zero evidence messages still arrive and enqueue.
- No-binding drop also Debug (line 234). No log on loop exit/fault. No channel-depth metric. No heartbeat.
- Render-success log is Information (line 297) but fires only on the happy path, so its *absence* is the only clue. `MqttMessagesReceived/Dropped` counters never increment once the drainer is dead.
- Net: a dead drainer looks identical to "broker delivered nothing" — which is exactly why the operator could only observe the RabbitMQ-side "peer closed TCP connection".

**Fix:** raise the received/enqueued log to Information (or add an Information counter in `OnMessageReceivedAsync`); `LogCritical` on any non-shutdown loop exit; add an OTEL gauge for the unbounded channel `Count`.

## Finding 4 — LOW (f): throw paths outside the per-button try/catch
- In `ProcessMessageAsync`, `_topicIndex.Lookup(topic)` (line 230) and `matches.Count` run *before* the per-button `try/catch` (which wraps only `RunPipeline`, lines 241–245).
- `ButtonTopicIndex.Lookup` (ButtonTopicIndex.cs:35–38) does `Select(...).ToList()` over a volatile dict (swap is atomic, lines 21/30); `_metrics?.MqttMessagesDropped.Add` (line 235) allocates a tag array.
- A throw there escapes `ProcessMessageAsync` but **is** caught by the drain-loop `try/catch` (223–224), so the loop survives. Not a loop-killer — but it is the class of throw that would be fatal if it occurred in the enumerator path. Finding 1's fix neutralizes it.

## Finding 5 — LOW (c/e): handler is safe; TCP close originates elsewhere
- (e) Handler subscribed once in ctor (lines 69–70) on one long-lived `IMqttClient` reused across reconnects (`ConnectWithRetryAsync` reuses `_client`, never recreates it). MQTTnet 5 does **not** clear `ApplicationMessageReceivedAsync` on disconnect → survives reconnect, no loss, no duplicate registration.
- (c) MQTTnet 5 tears down the connection if the handler Task **faults** (code comment lines 201–204, consistent with #829/#1646). But `OnMessageReceivedAsync` (lines 205–217) wraps its body in `try/catch` and unconditionally `return Task.CompletedTask` → it can never return a faulted Task → that teardown path is defused.
- Conclusion: the handler is NOT the cause of the "peer closed TCP connection". That close is more consistent with an off-thread fault (the dropped drain-task exception from Finding 1) or keepalive timing during the burst — investigate keepalive 30s vs RabbitMQ MQTT timeout. Add `UnobservedTaskException` logging to correlate.
