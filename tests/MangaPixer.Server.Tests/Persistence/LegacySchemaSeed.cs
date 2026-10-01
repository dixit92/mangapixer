namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

/// <summary>
/// Seeds rows into a database migrated only up to an OLDER migration (migration tests). An EF insert names every column of
/// the CURRENT model, so it fails as soon as a later migration adds a column to that table ("table catalog_nodes has no
/// column named ..."). This helper writes an entity with only the columns its table has right now, converted the way the
/// model stores them (e.g. DateTimeOffset as binary), and assigns a generated key back. Test-only.
/// </summary>
internal static class LegacySchemaSeed
{
    public static async Task InsertAsync<T>(MangaPixerDbContext db, T entity) where T : class
    {
        var entityType = db.Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped");
        var table = entityType.GetTableName()!;
        var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using (var info = conn.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                existing.Add(reader.GetString(1));
        }

        IProperty? generatedKey = null;
        var values = new List<(string Column, object? Value)>();
        foreach (var property in entityType.GetProperties())
        {
            var column = property.GetColumnName(store);
            if (column is null || !existing.Contains(column) || property.PropertyInfo is null)
                continue;
            var value = property.PropertyInfo.GetValue(entity);
            if (property.ValueGenerated == ValueGenerated.OnAdd && (value is null || value.Equals(Activator.CreateInstance(property.ClrType))))
            {
                generatedKey = property;
                continue;
            }
            var converter = property.GetTypeMapping().Converter;
            values.Add((column, value is null ? null : converter is null ? value : converter.ConvertToProvider(value)));
        }

        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", values.Select(v => $"\"{v.Column}\""))}) " +
                                 $"VALUES ({string.Join(", ", values.Select((_, i) => $"$p{i}"))})";
            for (var i = 0; i < values.Count; i++)
            {
                var p = insert.CreateParameter();
                p.ParameterName = $"$p{i}";
                p.Value = values[i].Value ?? DBNull.Value;
                insert.Parameters.Add(p);
            }
            await insert.ExecuteNonQueryAsync();
        }

        if (generatedKey?.PropertyInfo is { } keyProperty)
        {
            await using var rowId = conn.CreateCommand();
            rowId.CommandText = "SELECT last_insert_rowid()";
            var id = (long)(await rowId.ExecuteScalarAsync())!;
            keyProperty.SetValue(entity, Convert.ChangeType(id, generatedKey.ClrType));
        }
    }
}
