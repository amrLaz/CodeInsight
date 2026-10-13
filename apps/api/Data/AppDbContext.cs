using Microsoft.EntityFrameworkCore;

namespace CodeInsights.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries().Where(x => x.State == EntityState.Modified))
        {
            if (entry.Metadata.FindProperty("UpdatedAt") is not null)
            {
                entry.Property("UpdatedAt").CurrentValue = DateTime.UtcNow;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        IngestionModel.Configure(modelBuilder);
        NewCodeModel.Configure(modelBuilder);
        CodeScopeModel.Configure(modelBuilder);
        FindingIdentityModel.Configure(modelBuilder);
        CollaborationModel.Configure(modelBuilder);
        WorkItemModel.Configure(modelBuilder);
        PortfolioModel.Configure(modelBuilder);
        LicenseModel.Configure(modelBuilder);
        modelBuilder.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.Key)
                .HasColumnName("key")
                .HasColumnType("character varying(100)")
                .IsRequired();
            e.Property(x => x.Name)
                .HasColumnName("name")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasColumnType("text");
            e.Property(x => x.Archived)
                .HasColumnName("archived")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.OrganizationId)
                .HasColumnName("organization_id")
                .HasColumnType("character varying(36)");
            e.Property(x => x.AnalysisProfileId)
                .HasColumnName("analysis_profile_id")
                .HasColumnType("character varying(100)");
            e.Property(x => x.AnalysisProfileVersion)
                .HasColumnName("analysis_profile_version")
                .HasColumnType("character varying(50)");
            e.Property(x => x.MainBranch)
                .HasColumnName("main_branch")
                .HasColumnType("character varying(500)");
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<Branch>(e =>
        {
            e.ToTable("branches");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ProjectId)
                .HasColumnName("project_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.Name)
                .HasColumnName("name")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.IsDefault)
                .HasColumnName("is_default")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<AnalysisRecord>(e =>
        {
            e.ToTable("analysis_records");
            e.Property(x => x.AnalysisId)
                .HasColumnName("analysis_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ProjectKey)
                .HasColumnName("project_key")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.CommitSha)
                .HasColumnName("commit_sha")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.Branch)
                .HasColumnName("branch")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.CiProvider)
                .HasColumnName("ci_provider")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.ProviderRunId)
                .HasColumnName("provider_run_id")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.RepositoryUrl)
                .HasColumnName("repository_url")
                .HasColumnType("text")
                .IsRequired();
            e.Property(x => x.Status)
                .HasColumnName("status")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.QualityGate)
                .HasColumnName("quality_gate")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.MaintainabilityScore)
                .HasColumnName("maintainability_score")
                .HasColumnType("integer");
            e.Property(x => x.SecurityScore)
                .HasColumnName("security_score")
                .HasColumnType("integer");
            e.Property(x => x.ReliabilityScore)
                .HasColumnName("reliability_score")
                .HasColumnType("integer");
            e.Property(x => x.SecurityRating).HasColumnName("security_rating").HasColumnType("character varying(1)");
            e.Property(x => x.ReliabilityRating).HasColumnName("reliability_rating").HasColumnType("character varying(1)");
            e.Property(x => x.MaintainabilityRating).HasColumnName("maintainability_rating").HasColumnType("character varying(1)");
            e.Property(x => x.SecurityReviewRating).HasColumnName("security_review_rating").HasColumnType("character varying(1)");
            e.Property(x => x.CodeSmells).HasColumnName("code_smells").HasColumnType("integer");
            e.Property(x => x.Vulnerabilities)
                .HasColumnName("vulnerabilities")
                .HasColumnType("integer");
            e.Property(x => x.SecurityHotspots)
                .HasColumnName("security_hotspots")
                .HasColumnType("integer");
            e.Property(x => x.Bugs).HasColumnName("bugs").HasColumnType("integer");
            e.Property(x => x.Coverage).HasColumnName("coverage").HasColumnType("integer");
            e.Property(x => x.Duplication).HasColumnName("duplication").HasColumnType("integer");
            e.Property(x => x.DependencyVulnerabilities)
                .HasColumnName("dependency_vulnerabilities")
                .HasColumnType("integer");
            e.Property(x => x.Measurements)
                .HasColumnName("measurements")
                .HasColumnType("text")
                .IsRequired();
            e.Property(x => x.ToolExecutions)
                .HasColumnName("tool_executions")
                .HasColumnType("text")
                .IsRequired();
            e.Property(x => x.Completeness)
                .HasColumnName("completeness")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.ProfileId)
                .HasColumnName("profile_id")
                .HasColumnType("character varying(100)")
                .IsRequired();
            e.Property(x => x.ProfileVersion)
                .HasColumnName("profile_version")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.FormulaVersion)
                .HasColumnName("formula_version")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.GateSnapshot)
                .HasColumnName("gate_snapshot")
                .HasColumnType("text")
                .IsRequired();
            e.Property(x => x.AffectedFiles)
                .HasColumnName("affected_files")
                .HasColumnType("text")
                .IsRequired();
            e.Property(x => x.GateFailures)
                .HasColumnName("gate_failures")
                .HasColumnType("text")
                .IsRequired();
            e.Property(x => x.PrId).HasColumnName("pr_id").HasColumnType("character varying(64)");
            e.Property(x => x.PrRepoId)
                .HasColumnName("pr_repo_id")
                .HasColumnType("character varying(255)");
            e.Property(x => x.PrAdoProject)
                .HasColumnName("pr_ado_project")
                .HasColumnType("character varying(255)");
            e.Property(x => x.PrTargetBranch)
                .HasColumnName("pr_target_branch")
                .HasColumnType("character varying(255)");
            e.Property(x => x.PrAdoOrgUrl).HasColumnName("pr_ado_org_url").HasColumnType("text");
            e.Property(x => x.PrSourceSha)
                .HasColumnName("pr_source_sha")
                .HasColumnType("character varying(255)");
            e.Property(x => x.PrTargetSha)
                .HasColumnName("pr_target_sha")
                .HasColumnType("character varying(255)");
            e.Property(x => x.DecorationRevision)
                .HasColumnName("decoration_revision")
                .HasDefaultValue(0);
            e.Property(x => x.GitHubCheckRunId).HasColumnName("github_check_run_id");
            e.Property(x => x.ResultHash)
                .HasColumnName("result_hash")
                .HasColumnType("character varying(64)")
                .IsRequired();
            e.Property(x => x.FailureCode)
                .HasColumnName("failure_code")
                .HasColumnType("character varying(100)")
                .IsRequired();
            e.Property(x => x.QualityGateSummary)
                .HasColumnName("quality_gate_summary")
                .HasColumnType("text")
                .IsRequired();
            e.Property(x => x.ErrorCount).HasColumnName("error_count");
            e.Property(x => x.WarningCount).HasColumnName("warning_count");
            e.Property(x => x.InfoCount).HasColumnName("info_count");
            e.Property(x => x.PrDecorationStatus)
                .HasColumnName("pr_decoration_status")
                .HasColumnType("character varying(50)");
            e.Property(x => x.PrDecorationError)
                .HasColumnName("pr_decoration_error")
                .HasColumnType("text");
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("character varying(64)")
                .IsRequired();
            e.Property(x => x.StartedAt)
                .HasColumnName("started_at")
                .HasColumnType("timestamp with time zone");
            e.Property(x => x.CompletedAt)
                .HasColumnName("completed_at")
                .HasColumnType("timestamp with time zone");
            e.Property(x => x.DetailsPurged).HasColumnName("details_purged").HasDefaultValue(false);
            e.HasKey(x => x.AnalysisId);
            e.HasIndex(x => new { x.ProjectKey, x.ProviderRunId })
                .IsUnique()
                .HasFilter("provider_run_id <> ''");
        });
        modelBuilder.Entity<RetentionPolicy>(e =>
        {
            e.ToTable("retention_policies");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ProjectId)
                .HasColumnName("project_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.KeepAnalysesDays)
                .HasColumnName("keep_analyses_days")
                .HasColumnType("integer")
                .IsRequired();
            e.Property(x => x.KeepFailedAnalysesDays)
                .HasColumnName("keep_failed_analyses_days")
                .HasColumnType("integer")
                .IsRequired();
            e.Property(x => x.KeepMasterBranchDays)
                .HasColumnName("keep_master_branch_days")
                .HasColumnType("integer")
                .IsRequired();
            e.Property(x => x.AutoDeleteEnabled)
                .HasColumnName("auto_delete_enabled")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.LastCleanupAt)
                .HasColumnName("last_cleanup_at")
                .HasColumnType("timestamp with time zone");
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<QualityGateConfig>(e =>
        {
            e.ToTable("quality_gate_config");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.Name)
                .HasColumnName("name")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.IsDefault)
                .HasColumnName("is_default")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.MinMaintainabilityScore)
                .HasColumnName("min_maintainability_score")
                .HasColumnType("integer")
                .IsRequired();
            e.Property(x => x.MinSecurityScore)
                .HasColumnName("min_security_score")
                .HasColumnType("integer")
                .IsRequired();
            e.Property(x => x.MinReliabilityScore)
                .HasColumnName("min_reliability_score")
                .HasColumnType("integer")
                .IsRequired();
            e.Property(x => x.MaxCodeSmells)
                .HasColumnName("max_code_smells")
                .HasColumnType("integer")
                .IsRequired();
            e.Property(x => x.MaxVulnerabilities)
                .HasColumnName("max_vulnerabilities")
                .HasColumnType("integer")
                .IsRequired();
            e.Property(x => x.MaxSecurityHotspots)
                .HasColumnName("max_security_hotspots")
                .HasColumnType("integer")
                .IsRequired();
            e.Property(x => x.MaxBugs)
                .HasColumnName("max_bugs")
                .HasColumnType("integer")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<ProjectGateAssignment>(e =>
        {
            e.ToTable("project_gate_assignments");
            e.Property(x => x.ProjectKey)
                .HasColumnName("project_key")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.GateId)
                .HasColumnName("gate_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.AssignedAt)
                .HasColumnName("assigned_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.ProjectKey);
        });
        modelBuilder.Entity<AzureDevOpsOrganizationConfig>(e =>
        {
            e.ToTable("azure_devops_org_config");
            e.Property(x => x.ConfigId)
                .HasColumnName("config_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ConfigName)
                .HasColumnName("config_name")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.OrganizationUrl)
                .HasColumnName("organization_url")
                .HasColumnType("character varying(500)")
                .IsRequired();
            e.Property(x => x.Pat)
                .HasColumnName("pat")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.QualityGateEnabled)
                .HasColumnName("quality_gate_enabled")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.WorkItemsEnabled)
                .HasColumnName("work_items_enabled")
                .HasColumnType("boolean")
                .HasDefaultValue(false)
                .IsRequired();
            e.Property(x => x.RepoImportEnabled)
                .HasColumnName("repo_import_enabled")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.Status)
                .HasColumnName("status")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.LastTestedAt)
                .HasColumnName("last_tested_at")
                .HasColumnType("timestamp with time zone");
            e.Property(x => x.LastError).HasColumnName("last_error").HasColumnType("text");
            e.Property(x => x.BotIdentityId)
                .HasColumnName("bot_identity_id")
                .HasColumnType("character varying(255)");
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.ConfigId);
        });
        modelBuilder.Entity<AuthProviderConfig>(e =>
        {
            e.ToTable("auth_provider_config");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ProviderType)
                .HasColumnName("provider_type")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.Name)
                .HasColumnName("name")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.Config).HasColumnName("config").HasColumnType("text").IsRequired();
            e.Property(x => x.Enabled)
                .HasColumnName("enabled")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.Category)
                .HasColumnName("category")
                .HasColumnType("character varying(100)")
                .IsRequired();
            e.Property(x => x.ConnectionUrl)
                .HasColumnName("connection_url")
                .HasColumnType("character varying(500)")
                .IsRequired();
            e.Property(x => x.Status)
                .HasColumnName("status")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.LastTestedAt)
                .HasColumnName("last_tested_at")
                .HasColumnType("timestamp with time zone");
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<PlatformApiToken>(e =>
        {
            e.ToTable("platform_api_tokens");
            e.Property(x => x.TokenId)
                .HasColumnName("token_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.TokenName)
                .HasColumnName("token_name")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.TokenHash)
                .HasColumnName("token_hash")
                .HasColumnType("character varying(64)")
                .IsRequired();
            e.Property(x => x.TokenPreview)
                .HasColumnName("token_preview")
                .HasColumnType("character varying(16)")
                .IsRequired();
            e.Property(x => x.IsCurrent)
                .HasColumnName("is_current")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.Revoked)
                .HasColumnName("revoked")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamp with time zone");
            e.Property(x => x.Scopes).HasColumnName("scopes").HasColumnType("text").IsRequired();
            e.Property(x => x.OwnerUserId)
                .HasColumnName("owner_user_id")
                .HasColumnType("character varying(36)");
            e.HasKey(x => x.TokenId);
        });
        modelBuilder.Entity<ProjectIntegration>(e =>
        {
            e.ToTable("project_integrations");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ProjectKey)
                .HasColumnName("project_key")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.RepoProvider)
                .HasColumnName("repo_provider")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.RepoUrl)
                .HasColumnName("repo_url")
                .HasColumnType("character varying(500)")
                .IsRequired();
            e.Property(x => x.PipelineProvider)
                .HasColumnName("pipeline_provider")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.PipelineUrl)
                .HasColumnName("pipeline_url")
                .HasColumnType("character varying(500)")
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<RuleSuppression>(e =>
        {
            e.ToTable("rule_suppressions");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.RuleKey)
                .HasColumnName("rule_key")
                .HasColumnType("character varying(500)")
                .IsRequired();
            e.Property(x => x.SuppressedAt)
                .HasColumnName("suppressed_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.Note).HasColumnName("note").HasColumnType("text");
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<PlatformUser>(e =>
        {
            e.ToTable("platform_users");
            e.Property(x => x.UserId)
                .HasColumnName("user_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.Email)
                .HasColumnName("email")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.FullName)
                .HasColumnName("full_name")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.Role)
                .HasColumnName("role")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.HashedPassword)
                .HasColumnName("hashed_password")
                .HasColumnType("text");
            e.Property(x => x.AuthProvider)
                .HasColumnName("auth_provider")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.IsActive)
                .HasColumnName("is_active")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.LastLoginAt)
                .HasColumnName("last_login_at")
                .HasColumnType("timestamp with time zone");
            e.Property(x => x.EntraTenantId)
                .HasColumnName("entra_tenant_id")
                .HasColumnType("character varying(64)");
            e.Property(x => x.EntraObjectId)
                .HasColumnName("entra_object_id")
                .HasColumnType("character varying(64)");
            e.HasKey(x => x.UserId);
        });
        modelBuilder.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.Key)
                .HasColumnName("key")
                .HasColumnType("character varying(100)")
                .IsRequired();
            e.Property(x => x.Name)
                .HasColumnName("name")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<UserGrant>(e =>
        {
            e.ToTable("user_grants");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.UserId)
                .HasColumnName("user_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ScopeType)
                .HasColumnName("scope_type")
                .HasColumnType("character varying(20)")
                .IsRequired();
            e.Property(x => x.ScopeId)
                .HasColumnName("scope_id")
                .HasColumnType("character varying(100)")
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<EntraAccessGroup>(e =>
        {
            e.ToTable("entra_access_groups");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.TenantId)
                .HasColumnName("tenant_id")
                .HasColumnType("character varying(64)")
                .IsRequired();
            e.Property(x => x.ObjectId)
                .HasColumnName("object_id")
                .HasColumnType("character varying(64)")
                .IsRequired();
            e.Property(x => x.DisplayName)
                .HasColumnName("display_name")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.Role)
                .HasColumnName("role")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.IsActive)
                .HasColumnName("is_active")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<EntraGroupGrant>(e =>
        {
            e.ToTable("entra_group_grants");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.GroupId)
                .HasColumnName("group_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ScopeType)
                .HasColumnName("scope_type")
                .HasColumnType("character varying(20)")
                .IsRequired();
            e.Property(x => x.ScopeId)
                .HasColumnName("scope_id")
                .HasColumnType("character varying(100)")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<UserProjectFavorite>(e =>
        {
            e.ToTable("user_project_favorites");
            e.Property(x => x.UserId)
                .HasColumnName("user_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ProjectId)
                .HasColumnName("project_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => new { x.UserId, x.ProjectId });
        });
        modelBuilder.Entity<TokenProjectAccess>(e =>
        {
            e.ToTable("token_project_access");
            e.Property(x => x.TokenId)
                .HasColumnName("token_id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ProjectKey)
                .HasColumnName("project_key")
                .HasColumnType("character varying(100)")
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => new { x.TokenId, x.ProjectKey });
        });
        modelBuilder.Entity<OidcTransaction>(e =>
        {
            e.ToTable("oidc_transactions");
            e.Property(x => x.StateHash)
                .HasColumnName("state_hash")
                .HasColumnType("character varying(64)")
                .IsRequired();
            e.Property(x => x.CookieHash)
                .HasColumnName("cookie_hash")
                .HasColumnType("character varying(64)")
                .IsRequired();
            e.Property(x => x.Nonce)
                .HasColumnName("nonce")
                .HasColumnType("character varying(128)")
                .IsRequired();
            e.Property(x => x.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.UsedAt)
                .HasColumnName("used_at")
                .HasColumnType("timestamp with time zone");
            e.Property(x => x.IsSilent)
                .HasColumnName("is_silent")
                .HasColumnType("boolean")
                .HasDefaultValue(false)
                .IsRequired();
            e.HasKey(x => x.StateHash);
        });
        modelBuilder.Entity<PlatformSetting>(e =>
        {
            e.ToTable("platform_settings");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.Key)
                .HasColumnName("key")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.Value).HasColumnName("value").HasColumnType("text").IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasColumnType("text");
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<NotificationChannel>(e =>
        {
            e.ToTable("notification_channels");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.Name)
                .HasColumnName("name")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.ChannelType)
                .HasColumnName("channel_type")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.Config).HasColumnName("config").HasColumnType("text").IsRequired();
            e.Property(x => x.Enabled)
                .HasColumnName("enabled")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.OnFail)
                .HasColumnName("on_fail")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.OnPass)
                .HasColumnName("on_pass")
                .HasColumnType("boolean")
                .IsRequired();
            e.Property(x => x.OnSlaBreach)
                .HasColumnName("on_sla_breach")
                .HasColumnType("boolean")
                .HasDefaultValue(false)
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("character varying(36)")
                .IsRequired();
            e.Property(x => x.ActorEmail)
                .HasColumnName("actor_email")
                .HasColumnType("character varying(255)")
                .IsRequired();
            e.Property(x => x.ActorRole)
                .HasColumnName("actor_role")
                .HasColumnType("character varying(50)")
                .IsRequired();
            e.Property(x => x.Action)
                .HasColumnName("action")
                .HasColumnType("character varying(100)")
                .IsRequired();
            e.Property(x => x.ResourceType)
                .HasColumnName("resource_type")
                .HasColumnType("character varying(100)")
                .IsRequired();
            e.Property(x => x.ResourceId)
                .HasColumnName("resource_id")
                .HasColumnType("character varying(255)");
            e.Property(x => x.ResourceLabel)
                .HasColumnName("resource_label")
                .HasColumnType("character varying(500)");
            e.Property(x => x.Details).HasColumnName("details").HasColumnType("text");
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            e.HasKey(x => x.Id);
        });
        modelBuilder
            .Entity<Project>()
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.NoAction);
        modelBuilder
            .Entity<Branch>()
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);
        modelBuilder
            .Entity<RetentionPolicy>()
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);
        modelBuilder
            .Entity<ProjectGateAssignment>()
            .HasOne<QualityGateConfig>()
            .WithMany()
            .HasForeignKey(x => x.GateId)
            .OnDelete(DeleteBehavior.NoAction);
        modelBuilder
            .Entity<UserGrant>()
            .HasOne<PlatformUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.NoAction);
        modelBuilder
            .Entity<EntraGroupGrant>()
            .HasOne<EntraAccessGroup>()
            .WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder
            .Entity<UserProjectFavorite>()
            .HasOne<PlatformUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder
            .Entity<UserProjectFavorite>()
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder
            .Entity<TokenProjectAccess>()
            .HasOne<PlatformApiToken>()
            .WithMany()
            .HasForeignKey(x => x.TokenId)
            .OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<Project>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<Project>().HasIndex(x => x.OrganizationId);
        modelBuilder.Entity<Project>().HasIndex(x => x.Archived);
        modelBuilder.Entity<Branch>().HasIndex(x => x.ProjectId);
        modelBuilder.Entity<Branch>().HasIndex(x => new { x.ProjectId, x.Name });
        modelBuilder.Entity<Branch>().HasIndex(x => new { x.ProjectId, x.IsDefault });
        modelBuilder.Entity<AnalysisRecord>().HasIndex(x => x.ProjectKey);
        modelBuilder.Entity<AnalysisRecord>().HasIndex(x => x.CommitSha);
        modelBuilder.Entity<AnalysisRecord>().HasIndex(x => x.Branch);
        modelBuilder.Entity<AnalysisRecord>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<AnalysisRecord>().HasIndex(x => x.Status);
        modelBuilder.Entity<RetentionPolicy>().HasIndex(x => x.ProjectId).IsUnique();
        modelBuilder.Entity<AzureDevOpsOrganizationConfig>().HasIndex(x => x.ConfigName).IsUnique();
        modelBuilder
            .Entity<AzureDevOpsOrganizationConfig>()
            .HasIndex(x => x.OrganizationUrl)
            .IsUnique();
        modelBuilder.Entity<PlatformApiToken>().HasIndex(x => x.TokenHash);
        modelBuilder.Entity<PlatformApiToken>().HasIndex(x => new { x.IsCurrent, x.Revoked });
        modelBuilder.Entity<PlatformApiToken>().HasIndex(x => x.OwnerUserId);
        modelBuilder.Entity<ProjectIntegration>().HasIndex(x => x.ProjectKey).IsUnique();
        modelBuilder.Entity<RuleSuppression>().HasIndex(x => x.RuleKey).IsUnique();
        modelBuilder.Entity<PlatformUser>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<PlatformUser>().HasIndex(x => x.EntraTenantId);
        modelBuilder.Entity<PlatformUser>().HasIndex(x => x.EntraObjectId);
        modelBuilder.Entity<PlatformUser>().HasIndex(x => x.Role);
        modelBuilder
            .Entity<PlatformUser>()
            .HasIndex(x => new { x.EntraTenantId, x.EntraObjectId })
            .IsUnique();
        modelBuilder.Entity<Organization>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<UserGrant>().HasIndex(x => x.UserId);
        modelBuilder.Entity<UserGrant>().HasIndex(x => x.ScopeId);
        modelBuilder
            .Entity<UserGrant>()
            .HasIndex(x => new
            {
                x.UserId,
                x.ScopeType,
                x.ScopeId,
            })
            .IsUnique();
        modelBuilder
            .Entity<EntraAccessGroup>()
            .HasIndex(x => new { x.TenantId, x.ObjectId })
            .IsUnique();
        modelBuilder.Entity<EntraAccessGroup>().HasIndex(x => x.Role);
        modelBuilder.Entity<EntraGroupGrant>().HasIndex(x => x.GroupId);
        modelBuilder.Entity<EntraGroupGrant>().HasIndex(x => x.ScopeId);
        modelBuilder
            .Entity<EntraGroupGrant>()
            .HasIndex(x => new
            {
                x.GroupId,
                x.ScopeType,
                x.ScopeId,
            })
            .IsUnique();
        modelBuilder.Entity<UserProjectFavorite>().HasIndex(x => x.ProjectId);
        modelBuilder.Entity<TokenProjectAccess>().HasIndex(x => x.ProjectKey);
        modelBuilder.Entity<OidcTransaction>().HasIndex(x => x.CookieHash);
        modelBuilder.Entity<PlatformSetting>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<AuditLog>().HasIndex(x => x.ActorEmail);
        modelBuilder.Entity<AuditLog>().HasIndex(x => x.Action);
        modelBuilder.Entity<AuditLog>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<RetentionPolicy>().HasIndex(x => x.ProjectId).IsUnique();
    }
}
