using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;

namespace NodePilot.Api.Services.DbAdmin;

/// <summary>
/// Builds server-side EF Core queries dynamically for any entity type.
/// All filtering, ordering, and pagination happens in the database, not in memory.
/// </summary>
public static class DbAdminQueryBuilder
{
    private static readonly MethodInfo SetMethod =
        typeof(DbContext).GetMethods()
            .First(m => m.Name == "Set" && m.IsGenericMethod && m.GetParameters().Length == 0);

    /// <summary>
    /// Returns total row count + a page of rows serialized as dictionaries.
    /// Hidden columns are excluded from the output; masked values are replaced with "***".
    /// </summary>
    public static async Task<(long Total, List<Dictionary<string, object?>> Rows)> QueryAsync(
        NodePilotDbContext db,
        TableMeta table,
        int skip,
        int take,
        string? orderByColumn,
        bool descending,
        CancellationToken ct)
    {
        var clrType = table.EntityType.ClrType;
        var dbSet = SetMethod.MakeGenericMethod(clrType).Invoke(db, null) as IQueryable<object>
            ?? throw new InvalidOperationException($"Cannot resolve DbSet for {clrType.Name}");

        dbSet = dbSet.AsNoTracking();

        // Determine ordering column — default to first PK column
        var orderProp = orderByColumn ?? table.PkColumns.FirstOrDefault() ?? table.Columns[0].Name;
        var colMeta = table.Columns.FirstOrDefault(c => string.Equals(c.Name, orderProp, StringComparison.OrdinalIgnoreCase));
        if (colMeta is null || colMeta.IsHidden)
            orderProp = table.PkColumns.FirstOrDefault() ?? table.Columns.First(c => !c.IsHidden).Name;

        var total = await dbSet.LongCountAsync(ct);

        dbSet = ApplyOrderBy(dbSet, clrType, orderProp, descending);
        dbSet = dbSet.Skip(skip).Take(take);

        var materialised = await dbSet.ToListAsync(ct);

        var visibleColumns = table.Columns.Where(c => !c.IsHidden).ToList();
        var rows = new List<Dictionary<string, object?>>(materialised.Count);

        foreach (var entity in materialised)
        {
            var row = new Dictionary<string, object?>(visibleColumns.Count, StringComparer.Ordinal);
            foreach (var col in visibleColumns)
            {
                var prop = clrType.GetProperty(col.Name);
                if (prop is null) continue;
                var rawValue = prop.GetValue(entity);

                // Masked columns — replace value with "***" (value is present but obscured)
                var restriction = DbAdminPolicy.GetColumnRestriction(table.Name, col.Name);
                if (restriction.IsReadOnly && col.Name == "Value" && table.Name == "GlobalVariable")
                {
                    // GlobalVariable.Value: check IsSecret on the entity itself
                    var isSecretProp = clrType.GetProperty("IsSecret");
                    var isSecret = isSecretProp?.GetValue(entity) is true;
                    row[col.Name] = isSecret ? "***" : rawValue;
                }
                else
                {
                    row[col.Name] = rawValue;
                }
            }
            rows.Add(row);
        }

        return (total, rows);
    }

    /// <summary>
    /// Finds an entity by its primary key values.
    /// </summary>
    public static async Task<object?> FindByPkAsync(
        NodePilotDbContext db, TableMeta table, string[] pkValues, CancellationToken ct)
    {
        var clrType = table.EntityType.ClrType;
        var pkMetas = table.EntityType.FindPrimaryKey()?.Properties
            ?? throw new InvalidOperationException("No primary key");

        object[] keys;
        try
        {
            keys = pkMetas.Zip(pkValues)
                .Select(pair => CoerceToType(pair.Second, pair.First.ClrType))
                .ToArray();
        }
        catch
        {
            return null;
        }

        return await db.FindAsync(clrType, keys, ct);
    }

    /// <summary>
    /// Coerces a JSON <see cref="JsonElement"/> to the given CLR type.
    /// Throws <see cref="ArgumentException"/> on type mismatch (caller → 400).
    /// </summary>
    public static object? CoerceJsonValue(JsonElement element, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (element.ValueKind == JsonValueKind.Null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
                throw new ArgumentException($"Cannot set non-nullable type {targetType.Name} to null");
            return null;
        }

        try
        {
            if (underlying == typeof(string))    return element.GetString();
            if (underlying == typeof(bool))      return element.GetBoolean();
            if (underlying == typeof(int))       return element.GetInt32();
            if (underlying == typeof(long))      return element.GetInt64();
            if (underlying == typeof(double))    return element.GetDouble();
            if (underlying == typeof(decimal))   return element.GetDecimal();
            if (underlying == typeof(float))     return (float)element.GetDouble();
            if (underlying == typeof(short))     return element.GetInt16();
            if (underlying == typeof(Guid))
            {
                var s = element.GetString() ?? throw new ArgumentException("Guid cannot be null");
                return Guid.Parse(s);
            }
            if (underlying == typeof(DateTime))
            {
                var s = element.GetString() ?? throw new ArgumentException("DateTime cannot be null");
                var dt = DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
            if (underlying.IsEnum)
            {
                var s = element.GetString() ?? element.GetRawText();
                if (Enum.TryParse(underlying, s, ignoreCase: true, out var enumVal)) return enumVal;
                throw new ArgumentException($"'{s}' is not a valid {underlying.Name}");
            }
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException($"Cannot convert value to {underlying.Name}: {ex.Message}");
        }

        throw new ArgumentException($"Unsupported column type: {underlying.Name}");
    }

    private static IQueryable<object> ApplyOrderBy(
        IQueryable<object> query, Type clrType, string propertyName, bool descending)
    {
        var param = Expression.Parameter(typeof(object), "x");
        var cast = Expression.Convert(param, clrType);
        var prop = Expression.Property(cast, propertyName);
        var boxed = Expression.Convert(prop, typeof(object));
        var lambda = Expression.Lambda<Func<object, object>>(boxed, param);

        return descending
            ? query.OrderByDescending(lambda)
            : query.OrderBy(lambda);
    }

    private static object CoerceToType(string value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying == typeof(Guid))   return Guid.Parse(value);
        if (underlying == typeof(string)) return value;
        if (underlying == typeof(int))    return int.Parse(value);
        if (underlying == typeof(long))   return long.Parse(value);
        return Convert.ChangeType(value, underlying);
    }
}
