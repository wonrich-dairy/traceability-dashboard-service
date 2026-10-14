using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Pomelo.EntityFrameworkCore.MySql.Migrations.Internal;

namespace TraceabilityService.Api.Infrastructure.Persistence;

#pragma warning disable EF1001 // Pomelo's migrator is in an Internal namespace; it is what this extends.

/// <summary>
/// Pomelo's migrator, except that an idempotent script (the one the pipeline applies to
/// production) carries trigger statements outside its guards. Pomelo guards every statement with
/// a temporary stored procedure, and MySQL refuses CREATE TRIGGER inside one (error 1303), so the
/// script would stop at the first trigger. Here a migration's CREATE or DROP TRIGGER is left out
/// of the guarded statements and appended, unguarded, at the end of the script; it must be written
/// to be safe to run again (CREATE TRIGGER IF NOT EXISTS, DROP TRIGGER IF EXISTS), since it runs on
/// every deploy. Migrate() and non-idempotent scripts are unchanged.
/// </summary>
public partial class TriggerAwareMySqlMigrator(
    IMigrationsAssembly migrationsAssembly,
    IHistoryRepository historyRepository,
    IDatabaseCreator databaseCreator,
    IMigrationsSqlGenerator migrationsSqlGenerator,
    IRawSqlCommandBuilder rawSqlCommandBuilder,
    IMigrationCommandExecutor migrationCommandExecutor,
    IRelationalConnection connection,
    ISqlGenerationHelper sqlGenerationHelper,
    ICurrentDbContext currentContext,
    IModelRuntimeInitializer modelRuntimeInitializer,
    IDiagnosticsLogger<DbLoggerCategory.Migrations> logger,
    IRelationalCommandDiagnosticsLogger commandLogger,
    IDatabaseProvider databaseProvider,
    IMigrationsModelDiffer migrationsModelDiffer,
    IDesignTimeModel designTimeModel,
    IDbContextOptions contextOptions,
    IExecutionStrategy executionStrategy)
    : MySqlMigrator(
        migrationsAssembly, historyRepository, databaseCreator, migrationsSqlGenerator, rawSqlCommandBuilder,
        migrationCommandExecutor, connection, sqlGenerationHelper, currentContext, modelRuntimeInitializer, logger,
        commandLogger, databaseProvider, migrationsModelDiffer, designTimeModel, contextOptions, executionStrategy)
{
    // Filled while one idempotent script is generated, in the order the migrations produce them.
    private readonly List<string> _unguarded = [];

    public override string GenerateScript(
        string? fromMigration = null,
        string? toMigration = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        _unguarded.Clear();
        var script = base.GenerateScript(fromMigration, toMigration, options);
        if (_unguarded.Count == 0)
        {
            return script;
        }

        var builder = new StringBuilder(script);
        builder.AppendLine();
        builder.AppendLine("-- Triggers: MySQL cannot create one inside the stored procedure that guards each");
        builder.AppendLine("-- statement above, so they run unguarded and are written to be safe to run again.");
        foreach (var statement in _unguarded)
        {
            builder.AppendLine(statement.Trim());
            builder.AppendLine();
        }

        _unguarded.Clear();
        return builder.ToString();
    }

    protected override IReadOnlyList<MigrationCommand> GenerateUpSql(Migration migration, MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default) =>
        LiftTriggers(base.GenerateUpSql(migration, options), options);

    protected override IReadOnlyList<MigrationCommand> GenerateDownSql(Migration migration, Migration? previousMigration, MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default) =>
        LiftTriggers(base.GenerateDownSql(migration, previousMigration, options), options);

    private List<MigrationCommand> LiftTriggers(IReadOnlyList<MigrationCommand> commands, MigrationsSqlGenerationOptions options)
    {
        if (!options.HasFlag(MigrationsSqlGenerationOptions.Idempotent))
        {
            return [.. commands];
        }

        var guarded = new List<MigrationCommand>(commands.Count);
        foreach (var command in commands)
        {
            if (IsTriggerStatement(command.CommandText))
            {
                _unguarded.Add(command.CommandText);
            }
            else
            {
                guarded.Add(command);
            }
        }

        return guarded;
    }

    public static bool IsTriggerStatement(string sql) => TriggerStatement().IsMatch(sql);

    [GeneratedRegex(@"^\s*(CREATE|DROP)\s+TRIGGER\b", RegexOptions.IgnoreCase)]
    private static partial Regex TriggerStatement();
}

#pragma warning restore EF1001
