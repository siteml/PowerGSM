Imports System
Imports System.Collections.Generic
Imports Microsoft.EntityFrameworkCore
Imports Microsoft.EntityFrameworkCore.Design
Imports Microsoft.EntityFrameworkCore.Metadata.Builders

' ============================================================
'  GSM.Manager Data Layer
'
'  All entities, EF Core DbContext, fluent configurations,
'  and the design-time factory for EF migrations.
'
'  NOTE: Configuration class names use "EntityConfig" suffix
'  to avoid collisions with plugin DTO type names.
' ============================================================

Namespace GSM.Manager.Data

    ' ============================================================
    '  Entity classes
    ' ============================================================

    ''' <summary>
    ''' A managed machine running the GSM.Node service.
    ''' </summary>
    Public Class NodeEntity
        Public Property NodeId As String
        Public Property DisplayName As String
        Public Property HostAddress As String
        Public Property Port As Integer = 8765
        Public Property AuthToken As String
        Public Property IsEnabled As Boolean = True
        Public Property LastSeenUtc As DateTime
        Public Property OsDescription As String

        Public Overridable Property Installations As ICollection(Of InstallationEntity)
    End Class

    ''' <summary>
    ''' A set of game server files on a specific node.
    ''' One installation can serve multiple instances (e.g. Last Oasis).
    ''' </summary>
    Public Class InstallationEntity
        Public Property InstallationId As String
        Public Property GameId As String
        Public Property DisplayName As String
        Public Property NodeId As String
        Public Property InstallPath As String
        Public Property InstallMethod As String
        Public Property InstalledVersion As String
        Public Property SteamCredentialId As String
        Public Property ConfigJson As String
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime

        Public Overridable Property Node As NodeEntity
        Public Overridable Property Instances As ICollection(Of InstanceEntity)
    End Class

    ''' <summary>
    ''' A running (or configured) game server instance.
    ''' Belongs to exactly one installation.
    ''' </summary>
    Public Class InstanceEntity
        Public Property InstanceId As String
        Public Property InstallationId As String
        Public Property DisplayName As String
        Public Property GameId As String
        Public Property ConfigJson As String
        Public Property ExeOverride As String
        Public Property AutoStart As Boolean = False
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime

        Public Overridable Property Installation As InstallationEntity
    End Class

    ''' <summary>
    ''' A persisted automation rule.
    ''' Trigger, conditions, and action are stored as JSON.
    ''' </summary>
    Public Class AutomationRuleEntity
        Public Property RuleId As String
        Public Property RuleName As String
        Public Property IsEnabled As Boolean = True
        Public Property ScopeKind As String
        Public Property TargetId As String
        Public Property TriggerJson As String
        Public Property ConditionsJson As String
        Public Property ActionJson As String
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
    End Class

    ''' <summary>
    ''' Steam credentials encrypted with DPAPI.
    ''' Password is stored as a byte array — only decryptable
    ''' on the same Windows account that encrypted it.
    ''' </summary>
    Public Class SteamCredentialEntity
        Public Property CredentialId As String
        Public Property DisplayName As String
        Public Property Username As String
        Public Property EncryptedPassword As Byte()
        Public Property IsAnonymous As Boolean
    End Class

    ''' <summary>
    ''' Realm credentials for games that require them (e.g. Last Oasis).
    ''' Keys are DPAPI-encrypted.
    ''' </summary>
    Public Class RealmCredentialEntity
        Public Property CredentialId As String
        Public Property DisplayName As String
        Public Property GameId As String
        Public Property EncryptedCustomerKey As Byte()
        Public Property EncryptedProviderKey As Byte()
    End Class

    ''' <summary>
    ''' Notification plugin configuration (Discord bot tokens, webhook URLs, etc).
    ''' </summary>
    Public Class NotificationPluginEntity
        Public Property PluginId As String
        Public Property DisplayName As String
        Public Property IsEnabled As Boolean = True
        Public Property ConfigJson As String
    End Class

    ''' <summary>
    ''' Notification subscription — which events route to which plugin.
    ''' </summary>
    Public Class NotificationSubscriptionEntity
        Public Property SubscriptionId As String
        Public Property PluginId As String
        Public Property EventName As String
        Public Property ScopeKind As String
        Public Property TargetId As String
        Public Property RoutingHintsJson As String
        Public Property IsEnabled As Boolean = True
    End Class

    ''' <summary>
    ''' Execution history for automation rules.
    ''' </summary>
    Public Class RuleExecutionEntity
        Public Property ExecutionId As String
        Public Property RuleId As String
        Public Property StartedAtUtc As DateTime
        Public Property CompletedAtUtc As DateTime?
        Public Property TriggerReason As String
        Public Property ConditionResultsJson As String
        Public Property ActionResultJson As String
        Public Property WasSkipped As Boolean
        Public Property SkipReason As String
    End Class

    ' ============================================================
    '  DbContext
    ' ============================================================

    Public Class GsmDbContext
        Inherits DbContext

        Public Sub New(options As DbContextOptions(Of GsmDbContext))
            MyBase.New(options)
        End Sub

        Public Property Nodes As DbSet(Of NodeEntity)
        Public Property Installations As DbSet(Of InstallationEntity)
        Public Property Instances As DbSet(Of InstanceEntity)
        Public Property AutomationRules As DbSet(Of AutomationRuleEntity)
        Public Property SteamCredentials As DbSet(Of SteamCredentialEntity)
        Public Property RealmCredentials As DbSet(Of RealmCredentialEntity)
        Public Property NotificationPlugins As DbSet(Of NotificationPluginEntity)
        Public Property NotificationSubscriptions As DbSet(Of NotificationSubscriptionEntity)
        Public Property RuleExecutions As DbSet(Of RuleExecutionEntity)

        Protected Overrides Sub OnModelCreating(modelBuilder As ModelBuilder)
            modelBuilder.ApplyConfiguration(New NodeEntityConfig())
            modelBuilder.ApplyConfiguration(New InstallationEntityConfig())
            modelBuilder.ApplyConfiguration(New InstanceEntityConfig())
            modelBuilder.ApplyConfiguration(New AutomationRuleEntityConfig())
            modelBuilder.ApplyConfiguration(New SteamCredentialEntityConfig())
            modelBuilder.ApplyConfiguration(New RealmCredentialEntityConfig())
            modelBuilder.ApplyConfiguration(New NotificationPluginEntityConfig())
            modelBuilder.ApplyConfiguration(New NotificationSubscriptionEntityConfig())
            modelBuilder.ApplyConfiguration(New RuleExecutionEntityConfig())
        End Sub

    End Class

    ' ============================================================
    '  Fluent configurations (EntityConfig suffix avoids collisions)
    ' ============================================================

    Public Class NodeEntityConfig
        Implements IEntityTypeConfiguration(Of NodeEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of NodeEntity)) Implements IEntityTypeConfiguration(Of NodeEntity).Configure
            builder.HasKey(Function(n) n.NodeId)
            builder.Property(Function(n) n.DisplayName).IsRequired().HasMaxLength(200)
            builder.Property(Function(n) n.HostAddress).IsRequired().HasMaxLength(500)
        End Sub
    End Class

    Public Class InstallationEntityConfig
        Implements IEntityTypeConfiguration(Of InstallationEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of InstallationEntity)) Implements IEntityTypeConfiguration(Of InstallationEntity).Configure
            builder.HasKey(Function(i) i.InstallationId)
            builder.Property(Function(i) i.GameId).IsRequired().HasMaxLength(100)
            builder.Property(Function(i) i.InstallPath).IsRequired().HasMaxLength(1000)
            builder.HasOne(Function(i) i.Node).
                WithMany(Function(n) n.Installations).
                HasForeignKey(Function(i) i.NodeId)
        End Sub
    End Class

    Public Class InstanceEntityConfig
        Implements IEntityTypeConfiguration(Of InstanceEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of InstanceEntity)) Implements IEntityTypeConfiguration(Of InstanceEntity).Configure
            builder.HasKey(Function(i) i.InstanceId)
            builder.Property(Function(i) i.GameId).IsRequired().HasMaxLength(100)
            builder.HasOne(Function(i) i.Installation).
                WithMany(Function(inst) inst.Instances).
                HasForeignKey(Function(i) i.InstallationId)
        End Sub
    End Class

    Public Class AutomationRuleEntityConfig
        Implements IEntityTypeConfiguration(Of AutomationRuleEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of AutomationRuleEntity)) Implements IEntityTypeConfiguration(Of AutomationRuleEntity).Configure
            builder.HasKey(Function(r) r.RuleId)
            builder.Property(Function(r) r.RuleName).IsRequired().HasMaxLength(200)
        End Sub
    End Class

    Public Class SteamCredentialEntityConfig
        Implements IEntityTypeConfiguration(Of SteamCredentialEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of SteamCredentialEntity)) Implements IEntityTypeConfiguration(Of SteamCredentialEntity).Configure
            builder.HasKey(Function(c) c.CredentialId)
            builder.Property(Function(c) c.Username).IsRequired().HasMaxLength(200)
        End Sub
    End Class

    Public Class RealmCredentialEntityConfig
        Implements IEntityTypeConfiguration(Of RealmCredentialEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of RealmCredentialEntity)) Implements IEntityTypeConfiguration(Of RealmCredentialEntity).Configure
            builder.HasKey(Function(c) c.CredentialId)
            builder.Property(Function(c) c.GameId).IsRequired().HasMaxLength(100)
        End Sub
    End Class

    Public Class NotificationPluginEntityConfig
        Implements IEntityTypeConfiguration(Of NotificationPluginEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of NotificationPluginEntity)) Implements IEntityTypeConfiguration(Of NotificationPluginEntity).Configure
            builder.HasKey(Function(p) p.PluginId)
            builder.Property(Function(p) p.DisplayName).IsRequired().HasMaxLength(200)
        End Sub
    End Class

    Public Class NotificationSubscriptionEntityConfig
        Implements IEntityTypeConfiguration(Of NotificationSubscriptionEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of NotificationSubscriptionEntity)) Implements IEntityTypeConfiguration(Of NotificationSubscriptionEntity).Configure
            builder.HasKey(Function(s) s.SubscriptionId)
            builder.Property(Function(s) s.PluginId).IsRequired().HasMaxLength(100)
        End Sub
    End Class

    Public Class RuleExecutionEntityConfig
        Implements IEntityTypeConfiguration(Of RuleExecutionEntity)

        Public Sub Configure(builder As EntityTypeBuilder(Of RuleExecutionEntity)) Implements IEntityTypeConfiguration(Of RuleExecutionEntity).Configure
            builder.HasKey(Function(e) e.ExecutionId)
            builder.Property(Function(e) e.RuleId).IsRequired().HasMaxLength(100)
            builder.HasIndex(Function(e) e.RuleId)
            builder.HasIndex(Function(e) e.StartedAtUtc)
        End Sub
    End Class

    ' ============================================================
    '  Design-time factory (for EF migrations)
    ' ============================================================

    ''' <summary>
    ''' Required for EF Core Tools to create migrations.
    ''' If "No DbContext found" error, this is what fixes it.
    ''' </summary>
    Public Class GsmDbContextFactory
        Implements IDesignTimeDbContextFactory(Of GsmDbContext)

        Public Function CreateDbContext(args As String()) As GsmDbContext Implements IDesignTimeDbContextFactory(Of GsmDbContext).CreateDbContext
            Dim options = New DbContextOptionsBuilder(Of GsmDbContext)().
                UseSqlite("Data Source=gsm.db").Options
            Return New GsmDbContext(options)
        End Function
    End Class

    ' ============================================================
    '  Extension methods
    ' ============================================================

    ''' <summary>
    ''' Helper methods for common data operations.
    ''' </summary>
    Public Module GsmDataExtensions

        ''' <summary>
        ''' Creates and configures a GsmDbContext with the default SQLite path.
        ''' </summary>
        Public Function CreateDefaultContext() As GsmDbContext
            Dim options = New DbContextOptionsBuilder(Of GsmDbContext)().
                UseSqlite("Data Source=gsm.db").Options
            Return New GsmDbContext(options)
        End Function

        ''' <summary>
        ''' Creates and configures a GsmDbContext with a custom database path.
        ''' </summary>
        Public Function CreateContext(dbPath As String) As GsmDbContext
            Dim options = New DbContextOptionsBuilder(Of GsmDbContext)().
                UseSqlite($"Data Source={dbPath}").Options
            Return New GsmDbContext(options)
        End Function

    End Module

End Namespace
