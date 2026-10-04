using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.AI;
using OpenDeepWiki.Services.Admin;

namespace OpenDeepWiki.Infrastructure;

/// <summary>
/// 数据库初始化服务
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// 初始化数据库（创建默认角色和OAuth提供商）
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // 确保数据库已创建
        if (context is DbContext dbContext)
        {
            await dbContext.Database.EnsureCreatedAsync();
        }

        // 初始化默认角色
        await InitializeRolesAsync(context);

        // 初始化默认管理员账户
        await InitializeAdminUserAsync(context);

        // 初始化OAuth提供商
        await InitializeOAuthProvidersAsync(context);

        // Schema migrations for existing databases
        if (context is DbContext migrationCtx)
        {
            var isSqlite = migrationCtx.Database.ProviderName?.Contains("Sqlite",
                StringComparison.OrdinalIgnoreCase) == true;

            if (isSqlite)
            {
                await MigrateSqliteAsync(migrationCtx);
            }
            else
            {
                await MigratePostgresqlAsync(migrationCtx);
            }
        }

        // 初始化默认 MCP 提供商
        await InitializeMcpProvidersAsync(context);

        // 初始化系统设置默认值（仅在首次运行时从环境变量创建）
        await BackfillOpenAIResponsesModelProviderTypesAsync(context);

        await SystemSettingDefaults.InitializeDefaultsAsync(configuration, context);

        await MigrateAiConfigurationAsync(scope.ServiceProvider);

        await RefreshBundledSkillsAsync(scope.ServiceProvider);
    }

    private static async Task MigrateAiConfigurationAsync(IServiceProvider serviceProvider)
    {
        try
        {
            var migrationService = serviceProvider.GetRequiredService<IAiConfigurationMigrationService>();
            await migrationService.MigrateAsync();
        }
        catch (Exception ex)
        {
            var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("DbInitializer");
            logger?.LogWarning(ex, "Failed to migrate legacy AI settings");
        }
    }

    private static async Task RefreshBundledSkillsAsync(IServiceProvider serviceProvider)
    {
        try
        {
            var toolsService = serviceProvider.GetRequiredService<IAdminToolsService>();
            await toolsService.RefreshSkillsFromDiskAsync();
        }
        catch (Exception ex)
        {
            var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("DbInitializer");
            logger?.LogWarning(ex, "Failed to refresh bundled skills from disk");
        }
    }

    private static async Task BackfillOpenAIResponsesModelProviderTypesAsync(IContext context)
    {
        var models = await context.AiModelConfigs
            .Where(model => !model.IsDeleted)
            .ToListAsync();

        var changed = false;
        foreach (var model in models)
        {
            if (!string.IsNullOrWhiteSpace(model.ProviderType))
            {
                continue;
            }

            var providerType = AiProviderResolver.NormalizeModelProviderType(null, model.ModelId);
            if (providerType == null ||
                string.Equals(model.ProviderType, providerType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            model.ProviderType = providerType;
            model.UpdatedAt = DateTime.UtcNow;
            changed = true;
        }

        if (changed)
        {
            await context.SaveChangesAsync();
        }
    }

    private static async Task InitializeAdminUserAsync(IContext context)
    {
        const string adminEmail = "admin@routin.ai";
        const string adminPassword = "Admin@123";

        var exists = await context.Users.AnyAsync(u => u.Email == adminEmail && !u.IsDeleted);
        if (exists) return;

        var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "Admin" && !r.IsDeleted);
        if (adminRole == null) return;

        var adminUser = new User
        {
            Id = Guid.NewGuid().ToString(),
            Name = "admin",
            Email = adminEmail,
            Password = BCrypt.Net.BCrypt.HashPassword(adminPassword),
            Status = 1,
            IsSystem = true,
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(adminUser);

        var userRole = new UserRole
        {
            Id = Guid.NewGuid().ToString(),
            UserId = adminUser.Id,
            RoleId = adminRole.Id,
            CreatedAt = DateTime.UtcNow
        };

        context.UserRoles.Add(userRole);
        await context.SaveChangesAsync();
    }

    private static async Task InitializeRolesAsync(IContext context)
    {
        var roles = new[]
        {
            new Role
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Admin",
                Description = "系统管理员",
                IsActive = true,
                IsSystemRole = true,
                CreatedAt = DateTime.UtcNow
            },
            new Role
            {
                Id = Guid.NewGuid().ToString(),
                Name = "User",
                Description = "普通用户",
                IsActive = true,
                IsSystemRole = true,
                CreatedAt = DateTime.UtcNow
            }
        };

        foreach (var role in roles)
        {
            var exists = await context.Roles.AnyAsync(r => r.Name == role.Name && !r.IsDeleted);
            if (!exists)
            {
                context.Roles.Add(role);
            }
        }

        await context.SaveChangesAsync();
    }

    private static async Task InitializeOAuthProvidersAsync(IContext context)
    {
        var providers = new[]
        {
            new OAuthProvider
            {
                Id = Guid.NewGuid().ToString(),
                Name = "github",
                DisplayName = "GitHub",
                AuthorizationUrl = "https://github.com/login/oauth/authorize",
                TokenUrl = "https://github.com/login/oauth/access_token",
                UserInfoUrl = "https://api.github.com/user",
                ClientId = "YOUR_GITHUB_CLIENT_ID",
                ClientSecret = "YOUR_GITHUB_CLIENT_SECRET",
                RedirectUri = "http://localhost:8080/api/oauth/github/callback",
                Scope = "user:email",
                UserInfoMapping = "{\"id\":\"id\",\"name\":\"login\",\"email\":\"email\",\"avatar\":\"avatar_url\"}",
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            },
            new OAuthProvider
            {
                Id = Guid.NewGuid().ToString(),
                Name = "gitee",
                DisplayName = "Gitee",
                AuthorizationUrl = "https://gitee.com/oauth/authorize",
                TokenUrl = "https://gitee.com/oauth/token",
                UserInfoUrl = "https://gitee.com/api/v5/user",
                ClientId = "YOUR_GITEE_CLIENT_ID",
                ClientSecret = "YOUR_GITEE_CLIENT_SECRET",
                RedirectUri = "http://localhost:8080/api/oauth/gitee/callback",
                Scope = "user_info emails",
                UserInfoMapping = "{\"id\":\"id\",\"name\":\"name\",\"email\":\"email\",\"avatar\":\"avatar_url\"}",
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            }
        };

        foreach (var provider in providers)
        {
            var exists = await context.OAuthProviders.AnyAsync(p => p.Name == provider.Name && !p.IsDeleted);
            if (!exists)
            {
                context.OAuthProviders.Add(provider);
            }
        }

        await context.SaveChangesAsync();
    }

    private static async Task InitializeMcpProvidersAsync(IContext context)
    {
        const string providerName = "OpenDeepWiki Global MCP";
        const string globalMcpPath = "/api/mcp";

        var provider = await context.McpProviders
            .FirstOrDefaultAsync(p => p.Name == providerName);

        if (provider is { IsDeleted: true })
        {
            return;
        }

        if (provider is null)
        {
            context.McpProviders.Add(new McpProvider
            {
                Id = Guid.NewGuid().ToString(),
                Name = providerName,
                Description = "Global OpenDeepWiki MCP endpoint that routes questions across repositories.",
                ServerUrl = globalMcpPath,
                TransportType = "streamable_http",
                RequiresApiKey = false,
                IsActive = true,
                SortOrder = 0,
                AllowedTools = """["list_repositories","search_repositories","search_docs","read_doc"]""",
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            return;
        }

        var changed = false;
        if (provider.ServerUrl != globalMcpPath)
        {
            provider.ServerUrl = globalMcpPath;
            changed = true;
        }

        if (changed)
        {
            provider.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }

    private static async Task MigrateSqliteAsync(DbContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS GraphifyArtifacts (
                Id TEXT NOT NULL PRIMARY KEY,
                RepositoryId TEXT NOT NULL,
                RepositoryBranchId TEXT NOT NULL,
                Status INTEGER NOT NULL,
                CommitId TEXT,
                OutputRoot TEXT,
                EntryFilePath TEXT,
                GraphJsonPath TEXT,
                ReportPath TEXT,
                ErrorMessage TEXT,
                StartedAt TEXT,
                CompletedAt TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT,
                DeletedAt TEXT,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                Version BLOB,
                FOREIGN KEY (RepositoryId) REFERENCES Repositories(Id) ON DELETE CASCADE,
                FOREIGN KEY (RepositoryBranchId) REFERENCES RepositoryBranches(Id) ON DELETE CASCADE
            )");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_GraphifyArtifacts_RepositoryBranchId ON GraphifyArtifacts (RepositoryBranchId)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_GraphifyArtifacts_RepositoryId ON GraphifyArtifacts (RepositoryId)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_GraphifyArtifacts_Status_CreatedAt ON GraphifyArtifacts (Status, CreatedAt)");

        // Create ApiKeys table (SQLite types)
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ApiKeys (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                KeyPrefix TEXT NOT NULL,
                KeyHash TEXT NOT NULL,
                UserId TEXT NOT NULL,
                Scope TEXT NOT NULL DEFAULT 'mcp:read',
                ExpiresAt TEXT,
                LastUsedAt TEXT,
                LastUsedIp TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT,
                DeletedAt TEXT,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                Version BLOB,
                FOREIGN KEY (UserId) REFERENCES Users(Id)
            )");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_ApiKeys_KeyPrefix ON ApiKeys (KeyPrefix)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ApiKeys_UserId ON ApiKeys (UserId)");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS AiProviderConfigs (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                DisplayName TEXT,
                ProviderType TEXT NOT NULL,
                BaseUrl TEXT NOT NULL,
                ApiKey TEXT,
                AuthType TEXT NOT NULL DEFAULT 'ApiKey',
                IsBuiltIn INTEGER NOT NULL DEFAULT 0,
                IsActive INTEGER NOT NULL DEFAULT 1,
                SupportsModelDiscovery INTEGER NOT NULL DEFAULT 1,
                ModelsEndpoint TEXT,
                DefaultModelId TEXT,
                SystemProxyUrl TEXT,
                OAuthConfigJson TEXT,
                ChannelConfigJson TEXT,
                AccountsJson TEXT,
                RequestOverridesJson TEXT,
                IconUrl TEXT,
                Description TEXT,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT,
                DeletedAt TEXT,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                Version BLOB
            )");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_AiProviderConfigs_Name ON AiProviderConfigs (Name)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_AiProviderConfigs_IsActive ON AiProviderConfigs (IsActive)");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS AiModelConfigs (
                Id TEXT NOT NULL PRIMARY KEY,
                ProviderId TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                Name TEXT NOT NULL,
                DisplayName TEXT,
                ModelType TEXT NOT NULL DEFAULT 'chat',
                ProviderType TEXT,
                ContextWindow INTEGER,
                MaxOutputTokens INTEGER,
                InputTokenPrice TEXT,
                OutputTokenPrice TEXT,
                CacheHitTokenPrice TEXT,
                CacheCreationTokenPrice TEXT,
                SupportsThinking INTEGER NOT NULL DEFAULT 0,
                SupportsVision INTEGER NOT NULL DEFAULT 0,
                SupportsTools INTEGER NOT NULL DEFAULT 1,
                SupportsJsonMode INTEGER NOT NULL DEFAULT 0,
                IsDefault INTEGER NOT NULL DEFAULT 0,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CapabilitiesJson TEXT,
                ThinkingConfigJson TEXT,
                RequestOverridesJson TEXT,
                TagsJson TEXT,
                Description TEXT,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT,
                DeletedAt TEXT,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                Version BLOB
            )");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_AiModelConfigs_ProviderId_ModelId ON AiModelConfigs (ProviderId, ModelId)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_AiModelConfigs_IsActive ON AiModelConfigs (IsActive)");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS BranchGenerationTasks (
                Id TEXT NOT NULL PRIMARY KEY,
                RepositoryId TEXT NOT NULL,
                BranchId TEXT NOT NULL,
                Status INTEGER NOT NULL,
                Mode INTEGER NOT NULL,
                Priority INTEGER NOT NULL,
                IsManualTrigger INTEGER NOT NULL,
                RetryCount INTEGER NOT NULL,
                ErrorMessage TEXT,
                StartedAt TEXT,
                CompletedAt TEXT,
                RequestedBy TEXT,
                TargetCommitId TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT,
                DeletedAt TEXT,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                Version BLOB,
                FOREIGN KEY (RepositoryId) REFERENCES Repositories(Id) ON DELETE CASCADE,
                FOREIGN KEY (BranchId) REFERENCES RepositoryBranches(Id) ON DELETE CASCADE
            )");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_BranchGenerationTasks_BranchId_Status ON BranchGenerationTasks (BranchId, Status)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_BranchGenerationTasks_BranchId_Status_Mode ON BranchGenerationTasks (BranchId, Status, Mode) WHERE Status IN (0, 1)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_BranchGenerationTasks_RepositoryId_Status ON BranchGenerationTasks (RepositoryId, Status)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_BranchGenerationTasks_Status_Priority_CreatedAt ON BranchGenerationTasks (Status, Priority, CreatedAt)");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS RepositoryGenerationLocks (
                Id TEXT NOT NULL PRIMARY KEY,
                RepositoryId TEXT NOT NULL,
                OwnerType INTEGER NOT NULL,
                OwnerId TEXT NOT NULL,
                Scope INTEGER NOT NULL,
                AcquiredAt TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT,
                DeletedAt TEXT,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                Version BLOB,
                FOREIGN KEY (RepositoryId) REFERENCES Repositories(Id) ON DELETE CASCADE
            )");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS WikiGenerationSlots (
                Id TEXT NOT NULL PRIMARY KEY,
                SlotIndex INTEGER NOT NULL,
                WorkType INTEGER,
                RepositoryId TEXT,
                OwnerId TEXT,
                InstanceId TEXT,
                AcquiredAt TEXT,
                HeartbeatAt TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT,
                DeletedAt TEXT,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                Version BLOB
            )");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_WikiGenerationSlots_SlotIndex ON WikiGenerationSlots (SlotIndex)");

        // Add Description column if not exists
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "Description", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "GenerateSkill", "INTEGER NOT NULL DEFAULT 1");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "ScanDepthMode", "INTEGER NOT NULL DEFAULT 0");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "DirectoryTreeDepthOverride", "INTEGER");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "FileListDepthOverride", "INTEGER");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "MaxTreeNodes", "INTEGER");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "MaxFilesPerDirectory", "INTEGER");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "MaxTotalFiles", "INTEGER");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "ExtraExcludedDirsJson", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "ScanProfileHash", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "ScanProfileReason", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "ScanProfileConfidence", "REAL");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "ScanProfileUpdatedAt", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "BranchLanguages", "SkillGeneratedAt", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "BranchLanguages", "SkillMarkdown", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "ModelConfigs", "AiProviderId", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "ChatApps", "AiProviderId", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "AiModelConfigs", "ProviderType", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "AiModelConfigs", "CacheHitTokenPrice", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "AiModelConfigs", "CacheCreationTokenPrice", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "ProviderId", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "ProviderName", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "ProviderType", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "ModelId", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "CachedInputTokens", "INTEGER NOT NULL DEFAULT 0");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "CacheCreationInputTokens", "INTEGER NOT NULL DEFAULT 0");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "InputTokenPrice", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "OutputTokenPrice", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "CacheHitTokenPrice", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "CacheCreationTokenPrice", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "InputCost", "TEXT NOT NULL DEFAULT '0'");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "OutputCost", "TEXT NOT NULL DEFAULT '0'");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "TokenUsages", "TotalCost", "TEXT NOT NULL DEFAULT '0'");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "RepositoryBranches", "GenerationStatus", "INTEGER");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "RepositoryBranches", "LastGenerationTaskId", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "RepositoryBranches", "LastGenerationError", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "RepositoryBranches", "LastGenerationStartedAt", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "RepositoryBranches", "LastGenerationCompletedAt", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "RepositoryProcessingLogs", "BranchId", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "RepositoryProcessingLogs", "GenerationTaskId", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "RepositoryGenerationLocks", "InstanceId", "TEXT");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "RepositoryGenerationLocks", "HeartbeatAt", "TEXT");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_RepositoryProcessingLogs_RepositoryId_BranchId_GenerationTaskId_CreatedAt ON RepositoryProcessingLogs (RepositoryId, BranchId, GenerationTaskId, CreatedAt)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_RepositoryProcessingLogs_BranchId ON RepositoryProcessingLogs (BranchId)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_RepositoryProcessingLogs_GenerationTaskId ON RepositoryProcessingLogs (GenerationTaskId)");

        await CreateSqliteGitConnectionSchemaAsync(connection, ctx);
        await ExpandSqliteRepositoryOrchestrationSchemaAsync(connection, ctx);
        await CreateSqliteGitCredentialMigrationSchemaAsync(ctx);
    }

    /// <summary>
    /// Expand step for the legacy credential backfill progress table. It only adds objects.
    /// </summary>
    private static async Task CreateSqliteGitCredentialMigrationSchemaAsync(DbContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS GitCredentialMigrationRecords (
                Id TEXT NOT NULL CONSTRAINT PK_GitCredentialMigrationRecords PRIMARY KEY,
                RepositoryId TEXT NOT NULL,
                State INTEGER NOT NULL,
                ErrorCode TEXT NULL,
                GitConnectionId TEXT NULL,
                AttemptCount INTEGER NOT NULL,
                LastAttemptAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                CreatedAt TEXT NOT NULL,
                CONSTRAINT FK_GitCredentialMigrationRecords_Repositories_RepositoryId FOREIGN KEY (RepositoryId) REFERENCES Repositories (Id) ON DELETE CASCADE
            )");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_GitCredentialMigrationRecords_RepositoryId ON GitCredentialMigrationRecords (RepositoryId)");
    }

    /// <summary>
    /// Expand step for stable remote identity and branch-aware generation locks.
    /// The old single-column lock index is dropped: it would stop a second branch of one repository from locking.
    /// </summary>
    private static async Task ExpandSqliteRepositoryOrchestrationSchemaAsync(
        System.Data.Common.DbConnection connection,
        DbContext ctx)
    {
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "Provider", "INTEGER NULL");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "ProviderBaseUrl", "TEXT NULL");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "ProviderRepositoryId", "TEXT NULL");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "Repositories", "DefaultBranch", "TEXT NULL");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Repositories_RemoteIdentity ON Repositories (Provider, ProviderBaseUrl, ProviderRepositoryId) " +
            "WHERE \"ProviderRepositoryId\" IS NOT NULL AND NOT \"IsDeleted\"");

        // SQLite adds a foreign key to an existing table only together with a new nullable column.
        await AddSqliteColumnIfMissingAsync(
            connection, ctx, "RepositoryGenerationLocks", "BranchId",
            "TEXT NULL REFERENCES RepositoryBranches (Id) ON DELETE CASCADE");
        await ctx.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_RepositoryGenerationLocks_RepositoryId");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_RepositoryGenerationLocks_RepositoryScope ON RepositoryGenerationLocks (RepositoryId) " +
            "WHERE \"BranchId\" IS NULL");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_RepositoryGenerationLocks_RepositoryId_BranchId ON RepositoryGenerationLocks (RepositoryId, BranchId) " +
            "WHERE \"BranchId\" IS NOT NULL");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_RepositoryGenerationLocks_BranchId ON RepositoryGenerationLocks (BranchId)");

        if (await SqliteTableExistsAsync(connection, "IncrementalUpdateTasks"))
        {
            await AddSqliteColumnIfMissingAsync(connection, ctx, "IncrementalUpdateTasks", "RequestedBy", "TEXT NULL");
        }
    }

    private static async Task<bool> SqliteTableExistsAsync(System.Data.Common.DbConnection connection, string tableName)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        cmd.Parameters.Add(parameter);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    /// <summary>
    /// Expand step for shared Git connections. It only adds objects; legacy credential columns stay untouched.
    /// </summary>
    private static async Task CreateSqliteGitConnectionSchemaAsync(
        System.Data.Common.DbConnection connection,
        DbContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS GitConnections (
                Id TEXT NOT NULL CONSTRAINT PK_GitConnections PRIMARY KEY,
                Provider INTEGER NOT NULL,
                NormalizedServerUrl TEXT NOT NULL,
                ExternalAccountId TEXT NOT NULL,
                DisplayName TEXT NOT NULL DEFAULT '',
                AccountName TEXT NULL,
                ProtectedToken TEXT NOT NULL,
                CreatedByUserId TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL,
                LastValidatedAt TEXT NULL,
                LastValidationErrorCode TEXT NULL,
                ConcurrencyStamp TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NULL,
                DeletedAt TEXT NULL,
                IsDeleted INTEGER NOT NULL,
                Version BLOB NULL,
                CONSTRAINT FK_GitConnections_Users_CreatedByUserId FOREIGN KEY (CreatedByUserId) REFERENCES Users (Id) ON DELETE RESTRICT
            )");
        // Tables from the first expand step lack these two columns.
        await AddSqliteColumnIfMissingAsync(connection, ctx, "GitConnections", "DisplayName", "TEXT NOT NULL DEFAULT ''");
        await AddSqliteColumnIfMissingAsync(connection, ctx, "GitConnections", "ConcurrencyStamp", "TEXT NOT NULL DEFAULT ''");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_GitConnections_Identity ON GitConnections (Provider, NormalizedServerUrl, ExternalAccountId)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_GitConnections_CreatedByUserId ON GitConnections (CreatedByUserId)");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS GitConnectionAuditEvents (
                Id TEXT NOT NULL CONSTRAINT PK_GitConnectionAuditEvents PRIMARY KEY,
                GitConnectionId TEXT NOT NULL,
                ActorUserId TEXT NULL,
                EventType INTEGER NOT NULL,
                Outcome INTEGER NOT NULL,
                RepositoryId TEXT NULL,
                CorrelationId TEXT NULL,
                ErrorCode TEXT NULL,
                CreatedAt TEXT NOT NULL,
                CONSTRAINT FK_GitConnectionAuditEvents_GitConnections_GitConnectionId FOREIGN KEY (GitConnectionId) REFERENCES GitConnections (Id) ON DELETE RESTRICT
            )");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_GitConnectionAuditEvents_GitConnectionId_CreatedAt ON GitConnectionAuditEvents (GitConnectionId, CreatedAt)");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_GitConnectionAuditEvents_ActorUserId_CreatedAt ON GitConnectionAuditEvents (ActorUserId, CreatedAt)");

        // SQLite adds a foreign key to an existing table only together with a new nullable column.
        await AddSqliteColumnIfMissingAsync(
            connection, ctx, "Repositories", "GitConnectionId",
            "TEXT NULL REFERENCES GitConnections (Id) ON DELETE RESTRICT");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_Repositories_GitConnectionId ON Repositories (GitConnectionId)");
    }

    private static async Task AddSqliteColumnIfMissingAsync(
        System.Data.Common.DbConnection connection,
        DbContext ctx,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name=$name";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = columnName;
        cmd.Parameters.Add(parameter);

        var result = await cmd.ExecuteScalarAsync();
        if (Convert.ToInt64(result) == 0)
        {
            await ctx.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}");
        }
    }

    private static async Task MigratePostgresqlAsync(DbContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""GraphifyArtifacts"" (
                ""Id"" TEXT NOT NULL PRIMARY KEY,
                ""RepositoryId"" TEXT NOT NULL,
                ""RepositoryBranchId"" TEXT NOT NULL,
                ""Status"" INTEGER NOT NULL,
                ""CommitId"" TEXT,
                ""OutputRoot"" TEXT,
                ""EntryFilePath"" TEXT,
                ""GraphJsonPath"" TEXT,
                ""ReportPath"" TEXT,
                ""ErrorMessage"" TEXT,
                ""StartedAt"" TIMESTAMP WITH TIME ZONE,
                ""CompletedAt"" TIMESTAMP WITH TIME ZONE,
                ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL,
                ""UpdatedAt"" TIMESTAMP WITH TIME ZONE,
                ""DeletedAt"" TIMESTAMP WITH TIME ZONE,
                ""IsDeleted"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""Version"" BYTEA,
                FOREIGN KEY (""RepositoryId"") REFERENCES ""Repositories""(""Id"") ON DELETE CASCADE,
                FOREIGN KEY (""RepositoryBranchId"") REFERENCES ""RepositoryBranches""(""Id"") ON DELETE CASCADE
            )");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_GraphifyArtifacts_RepositoryBranchId"" ON ""GraphifyArtifacts"" (""RepositoryBranchId"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_GraphifyArtifacts_RepositoryId"" ON ""GraphifyArtifacts"" (""RepositoryId"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_GraphifyArtifacts_Status_CreatedAt"" ON ""GraphifyArtifacts"" (""Status"", ""CreatedAt"")");

        // Create ApiKeys table (PostgreSQL types)
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""ApiKeys"" (
                ""Id"" TEXT NOT NULL PRIMARY KEY,
                ""Name"" TEXT NOT NULL,
                ""KeyPrefix"" TEXT NOT NULL,
                ""KeyHash"" TEXT NOT NULL,
                ""UserId"" TEXT NOT NULL,
                ""Scope"" TEXT NOT NULL DEFAULT 'mcp:read',
                ""ExpiresAt"" TIMESTAMP WITH TIME ZONE,
                ""LastUsedAt"" TIMESTAMP WITH TIME ZONE,
                ""LastUsedIp"" TEXT,
                ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL,
                ""UpdatedAt"" TIMESTAMP WITH TIME ZONE,
                ""DeletedAt"" TIMESTAMP WITH TIME ZONE,
                ""IsDeleted"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""Version"" BYTEA,
                FOREIGN KEY (""UserId"") REFERENCES ""Users""(""Id"")
            )");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ApiKeys_KeyPrefix"" ON ""ApiKeys"" (""KeyPrefix"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_ApiKeys_UserId"" ON ""ApiKeys"" (""UserId"")");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""AiProviderConfigs"" (
                ""Id"" TEXT NOT NULL PRIMARY KEY,
                ""Name"" TEXT NOT NULL,
                ""DisplayName"" TEXT,
                ""ProviderType"" TEXT NOT NULL,
                ""BaseUrl"" TEXT NOT NULL,
                ""ApiKey"" TEXT,
                ""AuthType"" TEXT NOT NULL DEFAULT 'ApiKey',
                ""IsBuiltIn"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""IsActive"" BOOLEAN NOT NULL DEFAULT TRUE,
                ""SupportsModelDiscovery"" BOOLEAN NOT NULL DEFAULT TRUE,
                ""ModelsEndpoint"" TEXT,
                ""DefaultModelId"" TEXT,
                ""SystemProxyUrl"" TEXT,
                ""OAuthConfigJson"" TEXT,
                ""ChannelConfigJson"" TEXT,
                ""AccountsJson"" TEXT,
                ""RequestOverridesJson"" TEXT,
                ""IconUrl"" TEXT,
                ""Description"" TEXT,
                ""SortOrder"" INTEGER NOT NULL DEFAULT 0,
                ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL,
                ""UpdatedAt"" TIMESTAMP WITH TIME ZONE,
                ""DeletedAt"" TIMESTAMP WITH TIME ZONE,
                ""IsDeleted"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""Version"" BYTEA
            )");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AiProviderConfigs_Name"" ON ""AiProviderConfigs"" (""Name"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_AiProviderConfigs_IsActive"" ON ""AiProviderConfigs"" (""IsActive"")");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""AiModelConfigs"" (
                ""Id"" TEXT NOT NULL PRIMARY KEY,
                ""ProviderId"" TEXT NOT NULL,
                ""ModelId"" TEXT NOT NULL,
                ""Name"" TEXT NOT NULL,
                ""DisplayName"" TEXT,
                ""ModelType"" TEXT NOT NULL DEFAULT 'chat',
                ""ProviderType"" TEXT,
                ""ContextWindow"" INTEGER,
                ""MaxOutputTokens"" INTEGER,
                ""InputTokenPrice"" NUMERIC(18, 8),
                ""OutputTokenPrice"" NUMERIC(18, 8),
                ""CacheHitTokenPrice"" NUMERIC(18, 8),
                ""CacheCreationTokenPrice"" NUMERIC(18, 8),
                ""SupportsThinking"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""SupportsVision"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""SupportsTools"" BOOLEAN NOT NULL DEFAULT TRUE,
                ""SupportsJsonMode"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""IsDefault"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""IsActive"" BOOLEAN NOT NULL DEFAULT TRUE,
                ""CapabilitiesJson"" TEXT,
                ""ThinkingConfigJson"" TEXT,
                ""RequestOverridesJson"" TEXT,
                ""TagsJson"" TEXT,
                ""Description"" TEXT,
                ""SortOrder"" INTEGER NOT NULL DEFAULT 0,
                ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL,
                ""UpdatedAt"" TIMESTAMP WITH TIME ZONE,
                ""DeletedAt"" TIMESTAMP WITH TIME ZONE,
                ""IsDeleted"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""Version"" BYTEA
            )");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AiModelConfigs_ProviderId_ModelId"" ON ""AiModelConfigs"" (""ProviderId"", ""ModelId"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_AiModelConfigs_IsActive"" ON ""AiModelConfigs"" (""IsActive"")");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""BranchGenerationTasks"" (
                ""Id"" TEXT NOT NULL PRIMARY KEY,
                ""RepositoryId"" TEXT NOT NULL,
                ""BranchId"" TEXT NOT NULL,
                ""Status"" INTEGER NOT NULL,
                ""Mode"" INTEGER NOT NULL,
                ""Priority"" INTEGER NOT NULL,
                ""IsManualTrigger"" BOOLEAN NOT NULL,
                ""RetryCount"" INTEGER NOT NULL,
                ""ErrorMessage"" TEXT,
                ""StartedAt"" TIMESTAMP WITH TIME ZONE,
                ""CompletedAt"" TIMESTAMP WITH TIME ZONE,
                ""RequestedBy"" TEXT,
                ""TargetCommitId"" TEXT,
                ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL,
                ""UpdatedAt"" TIMESTAMP WITH TIME ZONE,
                ""DeletedAt"" TIMESTAMP WITH TIME ZONE,
                ""IsDeleted"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""Version"" BYTEA,
                FOREIGN KEY (""RepositoryId"") REFERENCES ""Repositories""(""Id"") ON DELETE CASCADE,
                FOREIGN KEY (""BranchId"") REFERENCES ""RepositoryBranches""(""Id"") ON DELETE CASCADE
            )");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_BranchGenerationTasks_BranchId_Status"" ON ""BranchGenerationTasks"" (""BranchId"", ""Status"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_BranchGenerationTasks_BranchId_Status_Mode"" ON ""BranchGenerationTasks"" (""BranchId"", ""Status"", ""Mode"") WHERE ""Status"" IN (0, 1)");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_BranchGenerationTasks_RepositoryId_Status"" ON ""BranchGenerationTasks"" (""RepositoryId"", ""Status"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_BranchGenerationTasks_Status_Priority_CreatedAt"" ON ""BranchGenerationTasks"" (""Status"", ""Priority"", ""CreatedAt"")");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""RepositoryGenerationLocks"" (
                ""Id"" TEXT NOT NULL PRIMARY KEY,
                ""RepositoryId"" TEXT NOT NULL,
                ""OwnerType"" INTEGER NOT NULL,
                ""OwnerId"" TEXT NOT NULL,
                ""Scope"" INTEGER NOT NULL,
                ""AcquiredAt"" TIMESTAMP WITH TIME ZONE NOT NULL,
                ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL,
                ""UpdatedAt"" TIMESTAMP WITH TIME ZONE,
                ""DeletedAt"" TIMESTAMP WITH TIME ZONE,
                ""IsDeleted"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""Version"" BYTEA,
                FOREIGN KEY (""RepositoryId"") REFERENCES ""Repositories""(""Id"") ON DELETE CASCADE
            )");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""WikiGenerationSlots"" (
                ""Id"" TEXT NOT NULL PRIMARY KEY,
                ""SlotIndex"" INTEGER NOT NULL,
                ""WorkType"" INTEGER,
                ""RepositoryId"" TEXT,
                ""OwnerId"" TEXT,
                ""InstanceId"" TEXT,
                ""AcquiredAt"" TIMESTAMP WITH TIME ZONE,
                ""HeartbeatAt"" TIMESTAMP WITH TIME ZONE,
                ""CreatedAt"" TIMESTAMP WITH TIME ZONE NOT NULL,
                ""UpdatedAt"" TIMESTAMP WITH TIME ZONE,
                ""DeletedAt"" TIMESTAMP WITH TIME ZONE,
                ""IsDeleted"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""Version"" BYTEA
            )");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_WikiGenerationSlots_SlotIndex"" ON ""WikiGenerationSlots"" (""SlotIndex"")");

        // Add Description column if not exists
        await ctx.Database.ExecuteSqlRawAsync(@"
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""Description"" TEXT;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""GenerateSkill"" BOOLEAN NOT NULL DEFAULT TRUE;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""ScanDepthMode"" INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""DirectoryTreeDepthOverride"" INTEGER;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""FileListDepthOverride"" INTEGER;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""MaxTreeNodes"" INTEGER;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""MaxFilesPerDirectory"" INTEGER;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""MaxTotalFiles"" INTEGER;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""ExtraExcludedDirsJson"" TEXT;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""ScanProfileHash"" TEXT;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""ScanProfileReason"" TEXT;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""ScanProfileConfidence"" NUMERIC(5, 4);
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""ScanProfileUpdatedAt"" TIMESTAMP WITH TIME ZONE;
            ALTER TABLE ""BranchLanguages"" ADD COLUMN IF NOT EXISTS ""SkillGeneratedAt"" TIMESTAMP WITH TIME ZONE;
            ALTER TABLE ""BranchLanguages"" ADD COLUMN IF NOT EXISTS ""SkillMarkdown"" TEXT;
            ALTER TABLE ""ModelConfigs"" ADD COLUMN IF NOT EXISTS ""AiProviderId"" TEXT;
            ALTER TABLE ""ChatApps"" ADD COLUMN IF NOT EXISTS ""AiProviderId"" TEXT;
            ALTER TABLE ""AiModelConfigs"" ADD COLUMN IF NOT EXISTS ""ProviderType"" TEXT;
            ALTER TABLE ""AiModelConfigs"" ADD COLUMN IF NOT EXISTS ""CacheHitTokenPrice"" NUMERIC(18, 8);
            ALTER TABLE ""AiModelConfigs"" ADD COLUMN IF NOT EXISTS ""CacheCreationTokenPrice"" NUMERIC(18, 8);
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""ProviderId"" TEXT;
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""ProviderName"" TEXT;
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""ProviderType"" TEXT;
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""ModelId"" TEXT;
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""CachedInputTokens"" INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""CacheCreationInputTokens"" INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""InputTokenPrice"" NUMERIC(18, 8);
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""OutputTokenPrice"" NUMERIC(18, 8);
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""CacheHitTokenPrice"" NUMERIC(18, 8);
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""CacheCreationTokenPrice"" NUMERIC(18, 8);
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""InputCost"" NUMERIC(18, 8) NOT NULL DEFAULT 0;
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""OutputCost"" NUMERIC(18, 8) NOT NULL DEFAULT 0;
            ALTER TABLE ""TokenUsages"" ADD COLUMN IF NOT EXISTS ""TotalCost"" NUMERIC(18, 8) NOT NULL DEFAULT 0;
            ALTER TABLE ""RepositoryBranches"" ADD COLUMN IF NOT EXISTS ""GenerationStatus"" INTEGER;
            ALTER TABLE ""RepositoryBranches"" ADD COLUMN IF NOT EXISTS ""LastGenerationTaskId"" TEXT;
            ALTER TABLE ""RepositoryBranches"" ADD COLUMN IF NOT EXISTS ""LastGenerationError"" TEXT;
            ALTER TABLE ""RepositoryBranches"" ADD COLUMN IF NOT EXISTS ""LastGenerationStartedAt"" TIMESTAMP WITH TIME ZONE;
            ALTER TABLE ""RepositoryBranches"" ADD COLUMN IF NOT EXISTS ""LastGenerationCompletedAt"" TIMESTAMP WITH TIME ZONE;
            ALTER TABLE ""RepositoryProcessingLogs"" ADD COLUMN IF NOT EXISTS ""BranchId"" TEXT;
            ALTER TABLE ""RepositoryProcessingLogs"" ADD COLUMN IF NOT EXISTS ""GenerationTaskId"" TEXT;
            ALTER TABLE ""RepositoryGenerationLocks"" ADD COLUMN IF NOT EXISTS ""InstanceId"" TEXT;
            ALTER TABLE ""RepositoryGenerationLocks"" ADD COLUMN IF NOT EXISTS ""HeartbeatAt"" TIMESTAMP WITH TIME ZONE;");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_RepositoryProcessingLogs_RepositoryId_BranchId_GenerationTaskId_CreatedAt"" ON ""RepositoryProcessingLogs"" (""RepositoryId"", ""BranchId"", ""GenerationTaskId"", ""CreatedAt"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_RepositoryProcessingLogs_BranchId"" ON ""RepositoryProcessingLogs"" (""BranchId"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_RepositoryProcessingLogs_GenerationTaskId"" ON ""RepositoryProcessingLogs"" (""GenerationTaskId"")");

        await CreatePostgresqlGitConnectionSchemaAsync(ctx);
        await ExpandPostgresqlRepositoryOrchestrationSchemaAsync(ctx);
        await CreatePostgresqlGitCredentialMigrationSchemaAsync(ctx);
    }

    /// <summary>
    /// Expand step for the legacy credential backfill progress table. It only adds objects.
    /// </summary>
    private static async Task CreatePostgresqlGitCredentialMigrationSchemaAsync(DbContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""GitCredentialMigrationRecords"" (
                ""Id"" character varying(36) NOT NULL,
                ""RepositoryId"" character varying(36) NOT NULL,
                ""State"" integer NOT NULL,
                ""ErrorCode"" character varying(64),
                ""GitConnectionId"" character varying(36),
                ""AttemptCount"" integer NOT NULL,
                ""LastAttemptAt"" timestamp with time zone NOT NULL,
                ""CompletedAt"" timestamp with time zone,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                CONSTRAINT ""PK_GitCredentialMigrationRecords"" PRIMARY KEY (""Id""),
                CONSTRAINT ""FK_GitCredentialMigrationRecords_Repositories_RepositoryId"" FOREIGN KEY (""RepositoryId"") REFERENCES ""Repositories"" (""Id"") ON DELETE CASCADE
            )");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_GitCredentialMigrationRecords_RepositoryId"" ON ""GitCredentialMigrationRecords"" (""RepositoryId"")");
    }

    /// <summary>
    /// Expand step for stable remote identity and branch-aware generation locks.
    /// The old single-column lock index is dropped: it would stop a second branch of one repository from locking.
    /// </summary>
    private static async Task ExpandPostgresqlRepositoryOrchestrationSchemaAsync(DbContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync(@"
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""Provider"" integer;
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""ProviderBaseUrl"" character varying(500);
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""ProviderRepositoryId"" character varying(128);
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""DefaultBranch"" character varying(200);");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Repositories_RemoteIdentity"" ON ""Repositories"" (""Provider"", ""ProviderBaseUrl"", ""ProviderRepositoryId"")
            WHERE ""ProviderRepositoryId"" IS NOT NULL AND NOT ""IsDeleted""");

        await ctx.Database.ExecuteSqlRawAsync(@"
            ALTER TABLE ""RepositoryGenerationLocks"" ADD COLUMN IF NOT EXISTS ""BranchId"" character varying(36)");
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'FK_RepositoryGenerationLocks_RepositoryBranches_BranchId'
                      AND conrelid = '""RepositoryGenerationLocks""'::regclass) THEN
                    ALTER TABLE ""RepositoryGenerationLocks""
                        ADD CONSTRAINT ""FK_RepositoryGenerationLocks_RepositoryBranches_BranchId""
                        FOREIGN KEY (""BranchId"") REFERENCES ""RepositoryBranches"" (""Id"") ON DELETE CASCADE;
                END IF;
            END
            $$");
        await ctx.Database.ExecuteSqlRawAsync(@"DROP INDEX IF EXISTS ""IX_RepositoryGenerationLocks_RepositoryId""");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_RepositoryGenerationLocks_RepositoryScope"" ON ""RepositoryGenerationLocks"" (""RepositoryId"")
            WHERE ""BranchId"" IS NULL");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_RepositoryGenerationLocks_RepositoryId_BranchId"" ON ""RepositoryGenerationLocks"" (""RepositoryId"", ""BranchId"")
            WHERE ""BranchId"" IS NOT NULL");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_RepositoryGenerationLocks_BranchId"" ON ""RepositoryGenerationLocks"" (""BranchId"")");

        await ctx.Database.ExecuteSqlRawAsync(@"
            ALTER TABLE IF EXISTS ""IncrementalUpdateTasks"" ADD COLUMN IF NOT EXISTS ""RequestedBy"" character varying(36)");
    }

    /// <summary>
    /// Expand step for shared Git connections. It only adds objects; legacy credential columns stay untouched.
    /// </summary>
    private static async Task CreatePostgresqlGitConnectionSchemaAsync(DbContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""GitConnections"" (
                ""Id"" text NOT NULL,
                ""Provider"" integer NOT NULL,
                ""NormalizedServerUrl"" character varying(500) NOT NULL,
                ""ExternalAccountId"" character varying(128) NOT NULL,
                ""DisplayName"" character varying(200) NOT NULL DEFAULT '',
                ""AccountName"" character varying(200),
                ""ProtectedToken"" text NOT NULL,
                ""CreatedByUserId"" character varying(36) NOT NULL,
                ""IsEnabled"" boolean NOT NULL,
                ""LastValidatedAt"" timestamp with time zone,
                ""LastValidationErrorCode"" character varying(64),
                ""ConcurrencyStamp"" character varying(36) NOT NULL DEFAULT '',
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""UpdatedAt"" timestamp with time zone,
                ""DeletedAt"" timestamp with time zone,
                ""IsDeleted"" boolean NOT NULL,
                ""Version"" bytea,
                CONSTRAINT ""PK_GitConnections"" PRIMARY KEY (""Id""),
                CONSTRAINT ""FK_GitConnections_Users_CreatedByUserId"" FOREIGN KEY (""CreatedByUserId"") REFERENCES ""Users"" (""Id"") ON DELETE RESTRICT
            )");
        // Tables from the first expand step lack these two columns.
        await ctx.Database.ExecuteSqlRawAsync(@"
            ALTER TABLE ""GitConnections"" ADD COLUMN IF NOT EXISTS ""DisplayName"" character varying(200) NOT NULL DEFAULT ''");
        await ctx.Database.ExecuteSqlRawAsync(@"
            ALTER TABLE ""GitConnections"" ADD COLUMN IF NOT EXISTS ""ConcurrencyStamp"" character varying(36) NOT NULL DEFAULT ''");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_GitConnections_Identity"" ON ""GitConnections"" (""Provider"", ""NormalizedServerUrl"", ""ExternalAccountId"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_GitConnections_CreatedByUserId"" ON ""GitConnections"" (""CreatedByUserId"")");

        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""GitConnectionAuditEvents"" (
                ""Id"" character varying(36) NOT NULL,
                ""GitConnectionId"" character varying(36) NOT NULL,
                ""ActorUserId"" character varying(36),
                ""EventType"" integer NOT NULL,
                ""Outcome"" integer NOT NULL,
                ""RepositoryId"" character varying(36),
                ""CorrelationId"" character varying(64),
                ""ErrorCode"" character varying(64),
                ""CreatedAt"" timestamp with time zone NOT NULL,
                CONSTRAINT ""PK_GitConnectionAuditEvents"" PRIMARY KEY (""Id""),
                CONSTRAINT ""FK_GitConnectionAuditEvents_GitConnections_GitConnectionId"" FOREIGN KEY (""GitConnectionId"") REFERENCES ""GitConnections"" (""Id"") ON DELETE RESTRICT
            )");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_GitConnectionAuditEvents_GitConnectionId_CreatedAt"" ON ""GitConnectionAuditEvents"" (""GitConnectionId"", ""CreatedAt"")");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_GitConnectionAuditEvents_ActorUserId_CreatedAt"" ON ""GitConnectionAuditEvents"" (""ActorUserId"", ""CreatedAt"")");

        await ctx.Database.ExecuteSqlRawAsync(@"
            ALTER TABLE ""Repositories"" ADD COLUMN IF NOT EXISTS ""GitConnectionId"" character varying(36)");
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'FK_Repositories_GitConnections_GitConnectionId'
                      AND conrelid = '""Repositories""'::regclass) THEN
                    ALTER TABLE ""Repositories""
                        ADD CONSTRAINT ""FK_Repositories_GitConnections_GitConnectionId""
                        FOREIGN KEY (""GitConnectionId"") REFERENCES ""GitConnections"" (""Id"") ON DELETE RESTRICT;
                END IF;
            END
            $$");
        await ctx.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX IF NOT EXISTS ""IX_Repositories_GitConnectionId"" ON ""Repositories"" (""GitConnectionId"")");
    }

    // ---- contract step: not called by InitializeAsync ----

    private static readonly string[] LegacyCredentialColumns = ["AuthAccount", "AuthPassword"];

    /// <summary>
    /// Contract step: removes the legacy repository credential columns.
    /// Nothing in the startup path calls this method. An operator runs it only after the backfill, one full update
    /// cycle, and verified database and key ring backups. It refuses to run while an active repository still
    /// depends on a legacy credential or holds a credential in its Git URL, and it does nothing when the columns are
    /// already gone. SQLite rebuilds the table so that every column, foreign key, and index survives.
    /// </summary>
    internal static async Task ApplyLegacyCredentialContractAsync(DbContext ctx, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var isSqlite = ctx.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        if (isSqlite)
        {
            await ApplySqliteLegacyCredentialContractAsync(ctx, cancellationToken);
        }
        else
        {
            await ApplyPostgresqlLegacyCredentialContractAsync(ctx, cancellationToken);
        }
    }

    private static InvalidOperationException ContractBlocked(long count)
        => new($"Legacy credential contract is blocked: {count} active repositories still depend on legacy credentials or hold a credential in the Git URL.");

    private static async Task ApplySqliteLegacyCredentialContractAsync(DbContext ctx, CancellationToken cancellationToken)
    {
        var connection = ctx.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var present = await SqliteLegacyColumnsAsync(connection, null, cancellationToken);
            if (present.Count == 0)
            {
                return;
            }

            // The pragma is a no-op inside a transaction, so it is set before the transaction starts.
            var foreignKeysWereOn = Convert.ToInt64(await SqliteScalarAsync(connection, null, "PRAGMA foreign_keys", cancellationToken)) != 0;
            await SqliteExecuteAsync(connection, null, "PRAGMA foreign_keys = OFF", cancellationToken);
            try
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    await RebuildSqliteRepositoriesWithoutLegacyColumnsAsync(connection, transaction, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }
            finally
            {
                if (foreignKeysWereOn)
                {
                    await SqliteExecuteAsync(connection, null, "PRAGMA foreign_keys = ON", CancellationToken.None);
                }
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task RebuildSqliteRepositoriesWithoutLegacyColumnsAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var present = await SqliteLegacyColumnsAsync(connection, transaction, cancellationToken);

        var credentialTest = string.Join(" OR ", present.Select(column => $"trim(coalesce({column}, '')) <> ''"));
        var blocked = Convert.ToInt64(await SqliteScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM Repositories WHERE IsDeleted = 0 AND " +
            $"((GitConnectionId IS NULL AND ({credentialTest})) OR GitUrl LIKE '%://%@%')", cancellationToken));
        if (blocked > 0)
        {
            throw ContractBlocked(blocked);
        }

        var createSql = (string?)await SqliteScalarAsync(connection, transaction,
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'Repositories'", cancellationToken)
            ?? throw new InvalidOperationException("The Repositories table does not exist.");
        var dependentSql = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT sql FROM sqlite_master WHERE tbl_name = 'Repositories' AND type IN ('index', 'trigger') AND sql IS NOT NULL ORDER BY type DESC, name";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                dependentSql.Add(reader.GetString(0));
            }
        }

        var keptColumns = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM pragma_table_info('Repositories') ORDER BY cid";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                if (!LegacyCredentialColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    keptColumns.Add(name);
                }
            }
        }

        var open = createSql.IndexOf('(');
        var close = createSql.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            throw new InvalidOperationException("The Repositories table definition cannot be read.");
        }

        var definitions = SplitSqliteDefinitions(createSql[(open + 1)..close])
            .Where(definition => !IsLegacyColumnDefinition(definition))
            .ToList();
        var rebuiltSql = $"CREATE TABLE \"Repositories__contract\" ({string.Join(",", definitions)}){createSql[(close + 1)..]}";
        var columnList = string.Join(", ", keptColumns.Select(column => $"\"{column}\""));

        await SqliteExecuteAsync(connection, transaction, rebuiltSql, cancellationToken);
        await SqliteExecuteAsync(connection, transaction,
            $"INSERT INTO \"Repositories__contract\" ({columnList}) SELECT {columnList} FROM \"Repositories\"", cancellationToken);

        var oldCount = Convert.ToInt64(await SqliteScalarAsync(connection, transaction, "SELECT COUNT(*) FROM \"Repositories\"", cancellationToken));
        var newCount = Convert.ToInt64(await SqliteScalarAsync(connection, transaction, "SELECT COUNT(*) FROM \"Repositories__contract\"", cancellationToken));
        if (oldCount != newCount)
        {
            throw new InvalidOperationException("The rebuilt Repositories table has a different row count.");
        }

        await SqliteExecuteAsync(connection, transaction, "DROP TABLE \"Repositories\"", cancellationToken);
        await SqliteExecuteAsync(connection, transaction, "ALTER TABLE \"Repositories__contract\" RENAME TO \"Repositories\"", cancellationToken);
        foreach (var sql in dependentSql)
        {
            await SqliteExecuteAsync(connection, transaction, sql, cancellationToken);
        }

        var violations = Convert.ToInt64(await SqliteScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM pragma_foreign_key_check", cancellationToken));
        if (violations > 0)
        {
            throw new InvalidOperationException("The rebuilt Repositories table breaks a foreign key.");
        }
    }

    private static async Task<List<string>> SqliteLegacyColumnsAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var present = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM pragma_table_info('Repositories') WHERE name IN ('AuthAccount', 'AuthPassword')";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            present.Add(reader.GetString(0));
        }

        return present;
    }

    private static async Task<object?> SqliteScalarAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }

    private static async Task SqliteExecuteAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Splits the body of a CREATE TABLE statement at the commas of its top level. Commas inside parentheses
    /// and inside quoted names or text do not split.
    /// </summary>
    private static List<string> SplitSqliteDefinitions(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        char? quote = null;
        for (var index = 0; index < body.Length; index++)
        {
            var character = body[index];
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }

                continue;
            }

            switch (character)
            {
                case '"' or '\'' or '`':
                    quote = character;
                    break;
                case '[':
                    quote = ']';
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(body[start..index]);
                    start = index + 1;
                    break;
            }
        }

        parts.Add(body[start..]);
        return parts;
    }

    private static bool IsLegacyColumnDefinition(string definition)
    {
        var text = definition.TrimStart();
        if (text.Length == 0)
        {
            return false;
        }

        string name;
        if (text[0] is '"' or '`' or '[')
        {
            var closing = text[0] == '[' ? ']' : text[0];
            var end = text.IndexOf(closing, 1);
            name = end < 0 ? text : text[1..end];
        }
        else
        {
            var end = text.IndexOfAny([' ', '\t', '\r', '\n']);
            name = end < 0 ? text : text[..end];
        }

        return LegacyCredentialColumns.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task ApplyPostgresqlLegacyCredentialContractAsync(DbContext ctx, CancellationToken cancellationToken)
    {
        var connection = ctx.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var present = new List<string>();
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() " +
                        "AND table_name = 'Repositories' AND column_name IN ('AuthAccount', 'AuthPassword')";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        present.Add(reader.GetString(0));
                    }
                }

                if (present.Count > 0)
                {
                    var credentialTest = string.Join(" OR ", present.Select(column => $"btrim(coalesce(\"{column}\", '')) <> ''"));
                    await using var gate = connection.CreateCommand();
                    gate.Transaction = transaction;
                    gate.CommandText =
                        "SELECT COUNT(*) FROM \"Repositories\" WHERE \"IsDeleted\" = FALSE AND " +
                        $"((\"GitConnectionId\" IS NULL AND ({credentialTest})) OR \"GitUrl\" LIKE '%://%@%')";
                    var blocked = Convert.ToInt64(await gate.ExecuteScalarAsync(cancellationToken));
                    if (blocked > 0)
                    {
                        throw ContractBlocked(blocked);
                    }

                    foreach (var column in LegacyCredentialColumns)
                    {
                        await using var drop = connection.CreateCommand();
                        drop.Transaction = transaction;
                        drop.CommandText = $"ALTER TABLE \"Repositories\" DROP COLUMN IF EXISTS \"{column}\"";
                        await drop.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }
}
