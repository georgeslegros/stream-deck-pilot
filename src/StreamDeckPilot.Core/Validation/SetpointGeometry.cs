using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Core.Models.Config;

namespace StreamDeckPilot.Core.Validation;

/// <summary>Where a setpoint's keys land on a device, and whether they fit (#132).</summary>
public static class SetpointGeometry
{
    /// <summary>
    /// Null when the three keys exist on the device and sit in one row (horizontal) or one column
    /// (vertical) without wrapping; otherwise why not.
    /// </summary>
    public static string? Error(SetpointControl control, DeviceEntry device)
    {
        var columns = device.KeyColumns;
        if (control.AnchorKey < 0 || control.AnchorKey >= device.KeyCount)
            return $"anchorKey {control.AnchorKey} is out of range for a device with {device.KeyCount} keys";

        if (control.Orientation == SetpointOrientation.Horizontal)
        {
            var column = control.AnchorKey % columns;
            if (column + 2 >= columns)
                return $"a horizontal setpoint needs 3 keys in one row; anchorKey {control.AnchorKey} is in column {column + 1} of {columns}";
        }
        else
        {
            var row = control.AnchorKey / columns;
            if (row + 2 >= device.KeyRows)
                return $"a vertical setpoint needs 3 keys in one column; anchorKey {control.AnchorKey} is in row {row + 1} of {device.KeyRows}";
        }
        return null;
    }
}
