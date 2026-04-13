Imports System.Collections.Generic
Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema
Imports System.Threading
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.EntityFrameworkCore.ChangeTracking
Imports Microsoft.EntityFrameworkCore.Metadata.Builders
Imports Microsoft.Extensions.DependencyInjection
Imports GSM.Plugin

' ============================================================
'  GSM Manager Database Context + Entity Classes
'  EF Core 8 · SQLite provider
'
'  Conventions:
'    - All PKs are String GUIDs (TEXT in SQLite)
'    - All FKs are nullable strings where the schema allows NULL
'    - Sensitive blobs (DPAPI-encrypted) are Byte() properties
'    - JSON blobs are String properties - serialisation handled
'      in the service layer, not in entity classes
'    - Shadow properties (CreatedAt, LastModifiedAt etc) are
'      set automatically by GsmDbContext.SaveChangesAsync
'
'  Usage:
'    Dim ctx = serviceProvider.GetRequiredService(Of GsmDbContext)()
'    Await ctx.Database.MigrateAsync()   ' On startup
' ============================================================

Namespace GSM.Data

    ' ============================================================
    '  DB CONTEXT
    ' ============================================================

    Public Class GsmDbContext
        Inherits DbContext

        Public Sub New(options As DbContextOptions(Of GsmDbContext))
            MyBase.New(options)
        End Sub

        ' ---- DbSets ----
        Public Property Nodes As DbSet(Of NodeEntity)
        Public Property SteamCredentials As DbSet(Of SteamCredentialEntity)
        Public Property RealmCredentials As DbSet(Of RealmCredentialEntity)
        Public Property Installations As DbSet(Of InstallationEntity)
        Public Property Instances As DbSet(Of InstanceEntity)
        Public Property AutomationRules As DbSet(Of AutomationRuleEntity)
        Public Property RuleExecutionHistory As DbSet(Of RuleExecutionHistoryEntity)
        Public Property NotificationPluginConfigs As DbSet(Of NotificationPluginConfigEntity)
        Public Property NotificationSubscriptions As DbSet(Of NotificationSubscriptionEntity)
        Public Property ManagerEventLog As DbSet(Of ManagerEventLogEntity)
        Public Property Settings As DbSet(Of SettingEntity)

        Protected Overrides Sub OnModelCreating(modelBuilder As ModelBuilder)
            modelBuilder.ApplyConfiguration(New NodeConfig())
            modelBuilder.ApplyConfiguration(New SteamCredentialConfig())
            modelBuilder.ApplyConfiguration(New RealmCredentialConfig())
            modelBuilder.ApplyConfiguration(New InstallationEntityConfig())
            modelBuilder.ApplyConfiguration(New InstanceEntityConfig())
            modelBuilder.ApplyConfiguration(New AutomationRuleConfig())
            modelBuilder.ApplyConfiguration(New RuleExecutionHistoryConfig())
            modelBuilder.ApplyConfiguration(New NotificationPluginConfigConfig())
            modelBuilder.ApplyConfiguration(New NotificationSubscriptionConfig())
            modelBuilder.ApplyConfiguration(New ManagerEventLogConfig())
            modelBuilder.ApplyConfiguration(New SettingConfig())
        End Sub

        ' Intercept saves to set timestamps automatically.
        Public Overrides Async Function SaveChangesAsync(
                Optional cancellationToken As CancellationToken = Nothing) As Task(Of Integer)

            Dim now = DateTime.UtcNow

            For Each trackedEntry As EntityEntry In ChangeTracker.Entries()
                If trackedEntry.State = EntityState.Added Then
                    If trackedEntry.Entity.GetType().GetProperty("CreatedAt") IsNot Nothing Then
                        trackedEntry.Property("CreatedAt").CurrentValue = now
                    End If
                    If trackedEntry.Entity.GetType().GetProperty("LastModifiedAt") IsNot Nothing Then
                        trackedEntry.Property("LastModifiedAt").CurrentValue = now
                    End If
                End If

                If trackedEntry.State = EntityState.Modified Then
                    If trackedEntry.Entity.GetType().GetProperty("LastModifiedAt") IsNot Nothing Then
                        trackedEntry.Property("LastModifiedAt").CurrentValue = now
                    End If
                End If
            Next

            Return Await MyBase.SaveChangesAsync(cancellationToken)
        End Function

    End Class


    ' ============================================================
    '  ENTITY CLASSES
    ' ============================================================

    Public Class NodeEntity
        Public Property NodeId As String
        Public Property DisplayName As String
        Public Property Hostname As String
        Public Property Port As Integer
        Public Property AuthToken As Byte()          ' DPAPI-encrypted
        Public Property Os As String
        Public Property IsEnabled As Boolean
        Public Property Notes As String
        Public Property AddedAt As DateTime
        Public Property LastSeenAt As DateTime?
        Public Property LastKnownVersion As String
        Public Property CachedHostname As String
        Public Property CachedCpuName As String
        Public Property CachedTotalMemoryMb As Long?
        ' Navigation
        Public Property Installations As List(Of InstallationEntity)
    End Class

    Public Class SteamCredentialEntity
        Public Property CredentialId As String
        Public Property DisplayName As String
        Public Property Username As String
        Public Property EncryptedPassword As Byte()  ' NULL if IsAnonymous
        Public Property IsAnonymous As Boolean
        Public Property GameId As String             ' Empty = any game
        Public Property Notes As String
        Public Property CreatedAt As DateTime
        Public Property LastUsedAt As DateTime?
        ' Navigation
        Public Property Installations As List(Of InstallationEntity)
    End Class

    Public Class RealmCredentialEntity
        Public Property CredentialId As String
        Public Property DisplayName As String
        Public Property GameId As String
        Public Property EncryptedCustomerKey As Byte()   ' DPAPI-encrypted
        Public Property EncryptedProviderKey As Byte()   ' DPAPI-encrypted
        Public Property Notes As String
        Public Property CreatedAt As DateTime
        Public Property LastUsedAt As DateTime?
        ' Navigation
        Public Property Installations As List(Of InstallationEntity)
        Public Property Instances As List(Of InstanceEntity)
    End Class

    Public Class InstallationEntity
        Public Property InstallationId As String
        Public Property DisplayName As String
        Public Property GameId As String
        Public Property NodeId As String
        Public Property InstallPath As String
        Public Property InstallMethod As String
        Public Property RealmCredentialId As String      ' Nullable FK
        Public Property SteamCredentialId As String      ' Nullable FK
        ' Game-specific config blob (JSON)
        Public Property PluginConfig As String
        Public Property InstalledVersion As String
        Public Property LastUpdatedAt As DateTime?
        Public Property LastValidatedAt As DateTime?
        Public Property IsEnabled As Boolean
        Public Property Notes As String
        Public Property CreatedAt As DateTime
        ' Installation lock state
        Public Property LockState As String
        Public Property WriteLockHeldSince As DateTime?
        Public Property WriteLockReason As String
        ' Navigation
        Public Property Node As NodeEntity
        Public Property RealmCredential As RealmCredentialEntity
        Public Property SteamCredential As SteamCredentialEntity
        Public Property Instances As List(Of InstanceEntity)
    End Class

    Public Class InstanceEntity
        Public Property InstanceId As String
        Public Property DisplayName As String
        Public Property GameId As String
        Public Property InstallationId As String
        Public Property RealmCredentialId As String      ' Nullable FK - overrides installation's
        Public Property ExeOverride As String
        ' Game-specific config blob (JSON)
        Public Property PluginConfig As String
        ' CrashRestartPolicy as JSON
        Public Property CrashRestartPolicy As String
        Public Property IsEnabled As Boolean
        Public Property Notes As String
        Public Property CreatedAt As DateTime
        Public Property SortOrder As Integer
        ' Last known state cached from node (node is source of truth)
        Public Property LastKnownState As String
        Public Property LastKnownRconState As String
        Public Property LastKnownPlayerCount As Integer
        Public Property LastStateReportAt As DateTime?
        ' Navigation
        Public Property Installation As InstallationEntity
        Public Property RealmCredential As RealmCredentialEntity
        Public Property NotificationSubscriptions As List(Of NotificationSubscriptionEntity)
    End Class

    Public Class AutomationRuleEntity
        Public Property RuleId As String
        Public Property DisplayName As String
        Public Property IsEnabled As Boolean
        Public Property Scope As String              ' "Instance", "Installation", "Global"
        Public Property TargetId As String           ' Empty if Global
        ' Serialised trigger/conditions/action (JSON)
        Public Property TriggerJson As String
        Public Property ConditionsJson As String
        Public Property ConditionMode As String      ' "All" or "Any"
        Public Property ActionJson As String
        Public Property OnConcurrentFire As String   ' "Skip", "Queue", "Cancel"
        Public Property CreatedAt As DateTime
        Public Property LastModifiedAt As DateTime
        Public Property LastFiredAt As DateTime?
        Public Property FireCount As Integer
        ' Navigation
        Public Property ExecutionHistory As List(Of RuleExecutionHistoryEntity)
    End Class

    Public Class RuleExecutionHistoryEntity
        Public Property ExecutionId As String
        Public Property RuleId As String
        Public Property ExecutedAt As DateTime
        Public Property TriggerSource As String
        ' Per-condition results as JSON array
        Public Property ConditionResultsJson As String
        Public Property ActionSuccess As Boolean
        Public Property ActionMessage As String
        ' Step-by-step action log as JSON array of strings
        Public Property ActionDetailsJson As String
        Public Property DurationMs As Long
        ' Navigation
        Public Property Rule As AutomationRuleEntity
    End Class

    Public Class NotificationPluginConfigEntity
        Public Property PluginConfigId As String
        Public Property PluginId As String           ' e.g. "discord" - unique
        Public Property DisplayName As String
        Public Property IsEnabled As Boolean
        ' Plugin-specific config as JSON. Sensitive fields within
        ' are encrypted by the plugin before storage.
        Public Property ConfigJson As String
        Public Property CreatedAt As DateTime
        Public Property LastModifiedAt As DateTime
    End Class

    Public Class NotificationSubscriptionEntity
        Public Property SubscriptionId As String
        Public Property PluginId As String
        Public Property Scope As String              ' "Instance" or "Installation"
        Public Property TargetId As String
        ' JSON array of NotificationEventType strings
        Public Property EventTypesJson As String
        Public Property RouteName As String
        Public Property IsEnabled As Boolean
        Public Property CreatedAt As DateTime
        ' Navigation
        Public Property Instance As InstanceEntity   ' Nullable - only set if Scope = Instance
    End Class

    Public Class ManagerEventLogEntity
        Public Property EventId As String
        Public Property OccurredAt As DateTime
        Public Property Severity As String           ' "Info", "Warning", "Error"
        Public Property Category As String
        Public Property TargetId As String
        Public Property TargetName As String
        Public Property Message As String
        Public Property Details As String
    End Class

    Public Class SettingEntity
        Public Property Key As String
        Public Property Value As String
        Public Property UpdatedAt As DateTime
    End Class


    ' ============================================================
    '  FLUENT CONFIGURATION
    '  One IEntityTypeConfiguration per entity.
    '  Defines table names, column types, indexes, and relationships
    '  explicitly rather than relying on conventions - keeps the
    '  schema predictable and migration-friendly.
    ' ============================================================

    Friend Class NodeConfig
        Implements IEntityTypeConfiguration(Of NodeEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of NodeEntity)) _
                Implements IEntityTypeConfiguration(Of NodeEntity).Configure

            builder.ToTable("Nodes")
            builder.HasKey(Function(e) e.NodeId)
            builder.Property(Function(e) e.NodeId).IsRequired()
            builder.Property(Function(e) e.DisplayName).IsRequired()
            builder.Property(Function(e) e.Hostname).IsRequired()
            builder.Property(Function(e) e.Port).IsRequired().HasDefaultValue(8765)
            builder.Property(Function(e) e.AuthToken).IsRequired()
            builder.Property(Function(e) e.Os).IsRequired()
            builder.Property(Function(e) e.IsEnabled).IsRequired().HasDefaultValue(True)
            builder.Property(Function(e) e.Notes).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.AddedAt).IsRequired()
            builder.HasIndex(Function(e) e.Hostname)
            builder.HasMany(Function(e) e.Installations).WithOne(Function(e) e.Node).HasForeignKey(Function(e) e.NodeId).OnDelete(DeleteBehavior.Restrict)
        End Sub
    End Class

    Friend Class SteamCredentialConfig
        Implements IEntityTypeConfiguration(Of SteamCredentialEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of SteamCredentialEntity)) _
                Implements IEntityTypeConfiguration(Of SteamCredentialEntity).Configure

            builder.ToTable("SteamCredentials")
            builder.HasKey(Function(e) e.CredentialId)
            builder.Property(Function(e) e.DisplayName).IsRequired()
            builder.Property(Function(e) e.Username).IsRequired()
            builder.Property(Function(e) e.IsAnonymous).IsRequired().HasDefaultValue(False)
            builder.Property(Function(e) e.GameId).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.Notes).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.CreatedAt).IsRequired()
            builder.HasIndex(Function(e) e.GameId)
        End Sub
    End Class

    Friend Class RealmCredentialConfig
        Implements IEntityTypeConfiguration(Of RealmCredentialEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of RealmCredentialEntity)) _
                Implements IEntityTypeConfiguration(Of RealmCredentialEntity).Configure

            builder.ToTable("RealmCredentials")
            builder.HasKey(Function(e) e.CredentialId)
            builder.Property(Function(e) e.DisplayName).IsRequired()
            builder.Property(Function(e) e.GameId).IsRequired()
            builder.Property(Function(e) e.EncryptedCustomerKey).IsRequired()
            builder.Property(Function(e) e.EncryptedProviderKey).IsRequired()
            builder.Property(Function(e) e.Notes).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.CreatedAt).IsRequired()
            builder.HasIndex(Function(e) e.GameId)
        End Sub
    End Class

    Friend Class InstallationEntityConfig
        Implements IEntityTypeConfiguration(Of InstallationEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of InstallationEntity)) _
                Implements IEntityTypeConfiguration(Of InstallationEntity).Configure

            builder.ToTable("Installations")
            builder.HasKey(Function(e) e.InstallationId)
            builder.Property(Function(e) e.DisplayName).IsRequired()
            builder.Property(Function(e) e.GameId).IsRequired()
            builder.Property(Function(e) e.NodeId).IsRequired()
            builder.Property(Function(e) e.InstallPath).IsRequired()
            builder.Property(Function(e) e.InstallMethod).IsRequired()
            builder.Property(Function(e) e.PluginConfig).IsRequired().HasDefaultValue("{}")
            builder.Property(Function(e) e.InstalledVersion).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.IsEnabled).IsRequired().HasDefaultValue(True)
            builder.Property(Function(e) e.Notes).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.CreatedAt).IsRequired()
            builder.Property(Function(e) e.LockState).IsRequired().HasDefaultValue("None")
            builder.Property(Function(e) e.WriteLockReason).IsRequired().HasDefaultValue("")
            builder.HasIndex(Function(e) e.NodeId)
            builder.HasIndex(Function(e) e.GameId)
            builder.HasOne(Function(e) e.RealmCredential).WithMany(Function(e) e.Installations).HasForeignKey(Function(e) e.RealmCredentialId).OnDelete(DeleteBehavior.SetNull)
            builder.HasOne(Function(e) e.SteamCredential).WithMany(Function(e) e.Installations).HasForeignKey(Function(e) e.SteamCredentialId).OnDelete(DeleteBehavior.SetNull)
            builder.HasMany(Function(e) e.Instances).WithOne(Function(e) e.Installation).HasForeignKey(Function(e) e.InstallationId).OnDelete(DeleteBehavior.Restrict)
        End Sub
    End Class

    Friend Class InstanceEntityConfig
        Implements IEntityTypeConfiguration(Of InstanceEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of InstanceEntity)) _
                Implements IEntityTypeConfiguration(Of InstanceEntity).Configure

            builder.ToTable("Instances")
            builder.HasKey(Function(e) e.InstanceId)
            builder.Property(Function(e) e.DisplayName).IsRequired()
            builder.Property(Function(e) e.GameId).IsRequired()
            builder.Property(Function(e) e.InstallationId).IsRequired()
            builder.Property(Function(e) e.ExeOverride).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.PluginConfig).IsRequired().HasDefaultValue("{}")
            builder.Property(Function(e) e.CrashRestartPolicy).IsRequired().HasDefaultValue("{}")
            builder.Property(Function(e) e.IsEnabled).IsRequired().HasDefaultValue(True)
            builder.Property(Function(e) e.Notes).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.CreatedAt).IsRequired()
            builder.Property(Function(e) e.SortOrder).IsRequired().HasDefaultValue(0)
            builder.Property(Function(e) e.LastKnownState).IsRequired().HasDefaultValue("Stopped")
            builder.Property(Function(e) e.LastKnownRconState).IsRequired().HasDefaultValue("NotAvailable")
            builder.Property(Function(e) e.LastKnownPlayerCount).IsRequired().HasDefaultValue(0)
            builder.HasIndex(Function(e) e.InstallationId)
            builder.HasIndex(Function(e) e.GameId)
            builder.HasOne(Function(e) e.RealmCredential).WithMany(Function(e) e.Instances).HasForeignKey(Function(e) e.RealmCredentialId).OnDelete(DeleteBehavior.SetNull)
        End Sub
    End Class

    Friend Class AutomationRuleConfig
        Implements IEntityTypeConfiguration(Of AutomationRuleEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of AutomationRuleEntity)) _
                Implements IEntityTypeConfiguration(Of AutomationRuleEntity).Configure

            builder.ToTable("AutomationRules")
            builder.HasKey(Function(e) e.RuleId)
            builder.Property(Function(e) e.DisplayName).IsRequired()
            builder.Property(Function(e) e.IsEnabled).IsRequired().HasDefaultValue(True)
            builder.Property(Function(e) e.Scope).IsRequired()
            builder.Property(Function(e) e.TargetId).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.TriggerJson).IsRequired()
            builder.Property(Function(e) e.ConditionsJson).IsRequired().HasDefaultValue("[]")
            builder.Property(Function(e) e.ConditionMode).IsRequired().HasDefaultValue("All")
            builder.Property(Function(e) e.ActionJson).IsRequired()
            builder.Property(Function(e) e.OnConcurrentFire).IsRequired().HasDefaultValue("Skip")
            builder.Property(Function(e) e.CreatedAt).IsRequired()
            builder.Property(Function(e) e.LastModifiedAt).IsRequired()
            builder.Property(Function(e) e.FireCount).IsRequired().HasDefaultValue(0)
            builder.HasIndex(Function(e) e.Scope)
            builder.HasIndex(Function(e) e.TargetId)
            builder.HasIndex(Function(e) e.IsEnabled)
            builder.HasMany(Function(e) e.ExecutionHistory).WithOne(Function(e) e.Rule).HasForeignKey(Function(e) e.RuleId).OnDelete(DeleteBehavior.Cascade)
        End Sub
    End Class

    Friend Class RuleExecutionHistoryConfig
        Implements IEntityTypeConfiguration(Of RuleExecutionHistoryEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of RuleExecutionHistoryEntity)) _
                Implements IEntityTypeConfiguration(Of RuleExecutionHistoryEntity).Configure

            builder.ToTable("RuleExecutionHistory")
            builder.HasKey(Function(e) e.ExecutionId)
            builder.Property(Function(e) e.RuleId).IsRequired()
            builder.Property(Function(e) e.ExecutedAt).IsRequired()
            builder.Property(Function(e) e.TriggerSource).IsRequired()
            builder.Property(Function(e) e.ConditionResultsJson).IsRequired().HasDefaultValue("[]")
            builder.Property(Function(e) e.ActionSuccess).IsRequired().HasDefaultValue(False)
            builder.Property(Function(e) e.ActionMessage).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.ActionDetailsJson).IsRequired().HasDefaultValue("[]")
            builder.Property(Function(e) e.DurationMs).IsRequired().HasDefaultValue(0L)
            builder.HasIndex(Function(e) e.RuleId)
            builder.HasIndex(Function(e) e.ExecutedAt)
        End Sub
    End Class

    Friend Class NotificationPluginConfigConfig
        Implements IEntityTypeConfiguration(Of NotificationPluginConfigEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of NotificationPluginConfigEntity)) _
                Implements IEntityTypeConfiguration(Of NotificationPluginConfigEntity).Configure

            builder.ToTable("NotificationPluginConfigs")
            builder.HasKey(Function(e) e.PluginConfigId)
            builder.Property(Function(e) e.PluginId).IsRequired()
            builder.HasIndex(Function(e) e.PluginId).IsUnique()
            builder.Property(Function(e) e.DisplayName).IsRequired()
            builder.Property(Function(e) e.IsEnabled).IsRequired().HasDefaultValue(True)
            builder.Property(Function(e) e.ConfigJson).IsRequired().HasDefaultValue("{}")
            builder.Property(Function(e) e.CreatedAt).IsRequired()
            builder.Property(Function(e) e.LastModifiedAt).IsRequired()
        End Sub
    End Class

    Friend Class NotificationSubscriptionConfig
        Implements IEntityTypeConfiguration(Of NotificationSubscriptionEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of NotificationSubscriptionEntity)) _
                Implements IEntityTypeConfiguration(Of NotificationSubscriptionEntity).Configure

            builder.ToTable("NotificationSubscriptions")
            builder.HasKey(Function(e) e.SubscriptionId)
            builder.Property(Function(e) e.PluginId).IsRequired()
            builder.Property(Function(e) e.Scope).IsRequired()
            builder.Property(Function(e) e.TargetId).IsRequired()
            builder.Property(Function(e) e.EventTypesJson).IsRequired().HasDefaultValue("[]")
            builder.Property(Function(e) e.RouteName).IsRequired().HasDefaultValue("")
            builder.Property(Function(e) e.IsEnabled).IsRequired().HasDefaultValue(True)
            builder.Property(Function(e) e.CreatedAt).IsRequired()
            builder.HasIndex(Function(e) e.TargetId)
            builder.HasIndex(Function(e) e.PluginId)
        End Sub
    End Class

    Friend Class ManagerEventLogConfig
        Implements IEntityTypeConfiguration(Of ManagerEventLogEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of ManagerEventLogEntity)) _
                Implements IEntityTypeConfiguration(Of ManagerEventLogEntity).Configure

            builder.ToTable("ManagerEventLog")
            builder.HasKey(Function(e) e.EventId)
            builder.Property(Function(e) e.OccurredAt).IsRequired()
            builder.Property(Function(e) e.Severity).IsRequired()
            builder.Property(Function(e) e.Category).IsRequired()
            builder.Property(Function(e) e.Message).IsRequired()
            builder.Property(Function(e) e.Details).IsRequired().HasDefaultValue("")
            builder.HasIndex(Function(e) e.OccurredAt)
            builder.HasIndex(Function(e) e.Category)
            builder.HasIndex(Function(e) e.TargetId)
        End Sub
    End Class

    Friend Class SettingConfig
        Implements IEntityTypeConfiguration(Of SettingEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of SettingEntity)) _
                Implements IEntityTypeConfiguration(Of SettingEntity).Configure

            builder.ToTable("Settings")
            builder.HasKey(Function(e) e.Key)
            builder.Property(Function(e) e.Value).IsRequired()
            builder.Property(Function(e) e.UpdatedAt).IsRequired()
            ' Seed default settings
            builder.HasData(
                New SettingEntity With {.Key = "PluginsDirectory",      .Value = "plugins", .UpdatedAt = New DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)},
                New SettingEntity With {.Key = "MetricsPollIntervalSec",.Value = "30",      .UpdatedAt = New DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)},
                New SettingEntity With {.Key = "VersionPollIntervalMin",.Value = "15",      .UpdatedAt = New DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)},
                New SettingEntity With {.Key = "LogBufferLineCap",      .Value = "10000",   .UpdatedAt = New DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)},
                New SettingEntity With {.Key = "RuleHistoryCapPerRule", .Value = "100",     .UpdatedAt = New DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)},
                New SettingEntity With {.Key = "NodeConnectTimeoutSec", .Value = "10",      .UpdatedAt = New DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)},
                New SettingEntity With {.Key = "NodeRequestTimeoutSec", .Value = "30",      .UpdatedAt = New DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)}
            )
        End Sub
    End Class


    ' ============================================================
    '  SERVICE REGISTRATION EXTENSION
    '  Call from your DI setup (Program.vb or App.vb):
    '
    '    services.AddGsmDatabase("Data Source=gsm.db")
    '
    '  The database file is created automatically on first run.
    '  Migrations are applied automatically on startup.
    ' ============================================================

    Public Module GsmDataExtensions

        <System.Runtime.CompilerServices.Extension>
        Public Function AddGsmDatabase(services As Microsoft.Extensions.DependencyInjection.IServiceCollection,
                                        connectionString As String) _
                As Microsoft.Extensions.DependencyInjection.IServiceCollection

            Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddDbContext(Of GsmDbContext)(
                services,
                Sub(options)
                    options.UseSqlite(connectionString,
                        Sub(sqliteOptions)
                            sqliteOptions.CommandTimeout(30)
                        End Sub)
                    options.EnableSensitiveDataLogging(False)
                End Sub)

            Return services
        End Function

        ' Apply pending migrations and seed default data.
        ' Call once at application startup before the UI shows.
        <System.Runtime.CompilerServices.Extension>
        Public Async Function InitialiseDatabaseAsync(
                services As System.IServiceProvider) As Task

            Using scope As IServiceScope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(services)
                Dim ctx = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService(Of GsmDbContext)(scope.ServiceProvider)
                Await ctx.Database.MigrateAsync()

                ' Enable WAL mode - EF Core doesn't set this automatically.
                Await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;")
                Await ctx.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;")
            End Using
        End Function

    End Module

End Namespace
