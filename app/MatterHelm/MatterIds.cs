using System.Globalization;

namespace MatterHelm;

/// <summary>
/// Parsing/formatting for Matter vendor and product ids (S10-4). Users meet
/// these as hex in the Google Home Developer Console (<c>0xFFF1</c>), but JSON
/// has no hex literal and a spin box wants a number — so both forms are
/// accepted everywhere and hex is what we display.
/// </summary>
public static class MatterIds
{
    /// <summary>Sanctioned Matter test vendor ids (ADR-002): 0xFFF1–0xFFF4.</summary>
    public const int TestVendorIdMin = 0xFFF1;

    /// <inheritdoc cref="TestVendorIdMin"/>
    public const int TestVendorIdMax = 0xFFF4;

    /// <summary>Sanctioned Matter test product ids (ADR-002): 0x8000–0x801F.</summary>
    public const int TestProductIdMin = 0x8000;

    /// <inheritdoc cref="TestProductIdMin"/>
    public const int TestProductIdMax = 0x801F;

    /// <summary>Parses "0xFFF1" (any case) or "65521" into 1–65535; false = unparsable or out of range.</summary>
    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        bool parsed = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        if (!parsed || value is < 1 or > 65535)
        {
            value = 0;
            return false;
        }

        return true;
    }

    /// <summary>Canonical display form: 4-digit uppercase hex with the <c>0x</c> prefix.</summary>
    public static string Format(int value) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{value:X4}");

    /// <summary>True when the pair sits in the sanctioned test ranges — outside is legal (a real allocated VID) but worth flagging.</summary>
    public static bool IsTestRange(int vendorId, int productId) =>
        vendorId is >= TestVendorIdMin and <= TestVendorIdMax
        && productId is >= TestProductIdMin and <= TestProductIdMax;
}
