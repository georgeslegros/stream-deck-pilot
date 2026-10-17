namespace StreamDeckPilot.Core.Rendering;

/// <summary>
/// A trend ready to draw: step points in time order, X from 0 (window start) to 1 (the moment the
/// series was taken), each value holding until the next point. The last value is carried forward
/// to X = 1. Min/Max span the values for vertical scaling.
/// </summary>
public sealed record SparklineSeries(IReadOnlyList<SparklinePoint> Points, double Min, double Max);

public readonly record struct SparklinePoint(double X, double Value);
