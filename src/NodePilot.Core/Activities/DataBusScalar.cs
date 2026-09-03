using System.Globalization;

namespace NodePilot.Core.Activities;

/// <summary>
/// Renders a CLR scalar as the text form the data bus uses.
///
/// <para>Every published value travels as a string, and the consumers — edge conditions,
/// templates, downstream request bodies — read it back with
/// <see cref="CultureInfo.InvariantCulture"/>. Producing it with the ambient culture writes
/// "1,5" for 1.5 on a de-DE host, which the invariant reader then accepts as 15. Activities
/// render through this helper so producer and consumer agree regardless of the host locale.</para>
/// </summary>
public static class DataBusScalar
{
    /// <summary>
    /// Invariant text for one value. Booleans render lowercase to match the rest of the data bus
    /// (<c>.success</c>, forEach items), timestamps round-trip in ISO-8601, and binary values
    /// become hex instead of a constant type name.
    /// </summary>
    public static string ToInvariantString(object? value) => value switch
    {
        null or DBNull => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}