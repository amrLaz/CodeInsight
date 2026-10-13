using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CodeInsights.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analysis_records",
                columns: table => new
                {
                    analysis_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    details_purged = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    project_key = table.Column<string>(type: "character varying(255)", nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(255)", nullable: false),
                    branch = table.Column<string>(type: "character varying(255)", nullable: false),
                    ci_provider = table.Column<string>(type: "character varying(50)", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", nullable: false),
                    quality_gate = table.Column<string>(type: "character varying(50)", nullable: false),
                    maintainability_score = table.Column<int>(type: "integer", nullable: true),
                    security_score = table.Column<int>(type: "integer", nullable: true),
                    reliability_score = table.Column<int>(type: "integer", nullable: true),
                    code_smells = table.Column<int>(type: "integer", nullable: true),
                    vulnerabilities = table.Column<int>(type: "integer", nullable: true),
                    security_hotspots = table.Column<int>(type: "integer", nullable: true),
                    bugs = table.Column<int>(type: "integer", nullable: true),
                    coverage = table.Column<int>(type: "integer", nullable: true),
                    duplication = table.Column<int>(type: "integer", nullable: true),
                    dependency_vulnerabilities = table.Column<int>(type: "integer", nullable: true),
                    measurements = table.Column<string>(type: "text", nullable: false),
                    tool_executions = table.Column<string>(type: "text", nullable: false),
                    completeness = table.Column<string>(type: "character varying(50)", nullable: false),
                    profile_id = table.Column<string>(type: "character varying(100)", nullable: false),
                    profile_version = table.Column<string>(type: "character varying(50)", nullable: false),
                    formula_version = table.Column<string>(type: "character varying(50)", nullable: false),
                    gate_snapshot = table.Column<string>(type: "text", nullable: false),
                    affected_files = table.Column<string>(type: "text", nullable: false),
                    gate_failures = table.Column<string>(type: "text", nullable: false),
                    pr_id = table.Column<string>(type: "character varying(64)", nullable: true),
                    pr_repo_id = table.Column<string>(type: "character varying(255)", nullable: true),
                    pr_ado_project = table.Column<string>(type: "character varying(255)", nullable: true),
                    pr_target_branch = table.Column<string>(type: "character varying(255)", nullable: true),
                    pr_decoration_status = table.Column<string>(type: "character varying(50)", nullable: true),
                    pr_decoration_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<string>(type: "character varying(64)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_records", x => x.analysis_id);
                });

            migrationBuilder.CreateTable(
                name: "archived_code_branches",
                columns: table => new
                {
                    project_key = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    archived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_archived_code_branches", x => new { x.project_key, x.branch });
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    actor_email = table.Column<string>(type: "character varying(255)", nullable: false),
                    actor_role = table.Column<string>(type: "character varying(50)", nullable: false),
                    action = table.Column<string>(type: "character varying(100)", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(100)", nullable: false),
                    resource_id = table.Column<string>(type: "character varying(255)", nullable: true),
                    resource_label = table.Column<string>(type: "character varying(500)", nullable: true),
                    details = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "auth_provider_config",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    provider_type = table.Column<string>(type: "character varying(50)", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", nullable: false),
                    config = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    category = table.Column<string>(type: "character varying(100)", nullable: false),
                    connection_url = table.Column<string>(type: "character varying(500)", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", nullable: false),
                    last_tested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_provider_config", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "azure_devops_org_config",
                columns: table => new
                {
                    config_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    config_name = table.Column<string>(type: "character varying(255)", nullable: false),
                    organization_url = table.Column<string>(type: "character varying(500)", nullable: false),
                    pat = table.Column<string>(type: "character varying(255)", nullable: false),
                    quality_gate_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    repo_import_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", nullable: false),
                    last_tested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_azure_devops_org_config", x => x.config_id);
                });

            migrationBuilder.CreateTable(
                name: "code_scope_assignments",
                columns: table => new
                {
                    project_key = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_scope_assignments", x => x.project_key);
                });

            migrationBuilder.CreateTable(
                name: "code_scope_versions",
                columns: table => new
                {
                    project_key = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    definition = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_scope_versions", x => new { x.project_key, x.version });
                });

            migrationBuilder.CreateTable(
                name: "collaboration_teams",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    project_key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    owner_user_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collaboration_teams", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "coverage_lines",
                columns: table => new
                {
                    ingestion_id = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    line = table.Column<int>(type: "integer", nullable: false),
                    covered = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coverage_lines", x => new { x.ingestion_id, x.path, x.line });
                });

            migrationBuilder.CreateTable(
                name: "durable_findings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    project_key = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    tool = table.Column<string>(type: "text", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: true),
                    line = table.Column<int>(type: "integer", nullable: true),
                    identity_kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    hotspot_review = table.Column<string>(type: "text", nullable: false),
                    assignee_user_id = table.Column<string>(type: "text", nullable: true),
                    team_id = table.Column<string>(type: "text", nullable: true),
                    exception_reason = table.Column<string>(type: "text", nullable: true),
                    exception_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_analysis_id = table.Column<string>(type: "text", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_durable_findings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "durable_jobs",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    available_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_durable_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "finding_decisions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    finding_id = table.Column<string>(type: "text", nullable: false),
                    project_key = table.Column<string>(type: "text", nullable: false),
                    actor_user_id = table.Column<string>(type: "text", nullable: false),
                    actor_email = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    snapshot = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finding_decisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "finding_identity_matches",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    project_key = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    analysis_id = table.Column<string>(type: "text", nullable: false),
                    ingestion_id = table.Column<string>(type: "text", nullable: false),
                    occurrence_identity = table.Column<string>(type: "text", nullable: false),
                    finding_id = table.Column<string>(type: "text", nullable: true),
                    method = table.Column<string>(type: "text", nullable: false),
                    base_sha = table.Column<string>(type: "text", nullable: false),
                    head_sha = table.Column<string>(type: "text", nullable: false),
                    old_path = table.Column<string>(type: "text", nullable: true),
                    old_line = table.Column<int>(type: "integer", nullable: true),
                    path = table.Column<string>(type: "text", nullable: true),
                    line = table.Column<int>(type: "integer", nullable: true),
                    evidence_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finding_identity_matches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "finding_occurrences",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    finding_id = table.Column<string>(type: "text", nullable: true),
                    ingestion_id = table.Column<string>(type: "text", nullable: false),
                    analysis_id = table.Column<string>(type: "text", nullable: false),
                    report_id = table.Column<string>(type: "text", nullable: false),
                    tool = table.Column<string>(type: "text", nullable: false),
                    original_tool = table.Column<string>(type: "text", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    original_severity = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: true),
                    line = table.Column<int>(type: "integer", nullable: true),
                    fingerprint = table.Column<string>(type: "text", nullable: true),
                    excepted = table.Column<bool>(type: "boolean", nullable: false),
                    evidence = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finding_occurrences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_project_policies",
                columns: table => new
                {
                    project_key = table.Column<string>(type: "text", nullable: false),
                    profile_id = table.Column<string>(type: "text", nullable: false),
                    profile_version = table.Column<string>(type: "text", nullable: false),
                    enforcement = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_project_policies", x => x.project_key);
                });

            migrationBuilder.CreateTable(
                name: "ingestions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    analysis_id = table.Column<string>(type: "text", nullable: false),
                    project_key = table.Column<string>(type: "text", nullable: false),
                    build_key = table.Column<string>(type: "text", nullable: false),
                    manifest_hash = table.Column<string>(type: "text", nullable: false),
                    manifest = table.Column<string>(type: "text", nullable: false),
                    profile_snapshot = table.Column<string>(type: "text", nullable: false),
                    exception_snapshot = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    technical_state = table.Column<string>(type: "text", nullable: false),
                    quality_gate = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: false),
                    gate_snapshot = table.Column<string>(type: "text", nullable: false),
                    comparison = table.Column<string>(type: "text", nullable: false),
                    code_data = table.Column<string>(type: "text", nullable: false),
                    code_scope_snapshot = table.Column<string>(type: "text", nullable: false),
                    security_review_snapshot = table.Column<string>(type: "text", nullable: false),
                    details_purged = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_channels",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", nullable: false),
                    channel_type = table.Column<string>(type: "character varying(50)", nullable: false),
                    config = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    on_fail = table.Column<bool>(type: "boolean", nullable: false),
                    on_pass = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "oidc_transactions",
                columns: table => new
                {
                    state_hash = table.Column<string>(type: "character varying(64)", nullable: false),
                    cookie_hash = table.Column<string>(type: "character varying(64)", nullable: false),
                    nonce = table.Column<string>(type: "character varying(128)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oidc_transactions", x => x.state_hash);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_api_tokens",
                columns: table => new
                {
                    token_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    token_name = table.Column<string>(type: "character varying(255)", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", nullable: false),
                    token_preview = table.Column<string>(type: "character varying(16)", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    revoked = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_api_tokens", x => x.token_id);
                });

            migrationBuilder.CreateTable(
                name: "platform_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    key = table.Column<string>(type: "character varying(255)", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_users",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    email = table.Column<string>(type: "character varying(255)", nullable: false),
                    full_name = table.Column<string>(type: "character varying(255)", nullable: false),
                    role = table.Column<string>(type: "character varying(50)", nullable: false),
                    hashed_password = table.Column<string>(type: "text", nullable: true),
                    auth_provider = table.Column<string>(type: "character varying(50)", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    entra_tenant_id = table.Column<string>(type: "character varying(64)", nullable: true),
                    entra_object_id = table.Column<string>(type: "character varying(64)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_users", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "profile_exceptions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    project_key = table.Column<string>(type: "text", nullable: false),
                    tool = table.Column<string>(type: "text", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_exceptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "profile_versions",
                columns: table => new
                {
                    profile_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    definition = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_versions", x => new { x.profile_id, x.version });
                });

            migrationBuilder.CreateTable(
                name: "project_integrations",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    project_key = table.Column<string>(type: "character varying(255)", nullable: false),
                    repo_provider = table.Column<string>(type: "character varying(50)", nullable: false),
                    repo_url = table.Column<string>(type: "character varying(500)", nullable: false),
                    pipeline_provider = table.Column<string>(type: "character varying(50)", nullable: false),
                    pipeline_url = table.Column<string>(type: "character varying(500)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_integrations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_ownerships",
                columns: table => new
                {
                    project_key = table.Column<string>(type: "text", nullable: false),
                    team_id = table.Column<string>(type: "text", nullable: true),
                    owner_user_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_ownerships", x => x.project_key);
                });

            migrationBuilder.CreateTable(
                name: "quality_gate_config",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    min_maintainability_score = table.Column<int>(type: "integer", nullable: false),
                    min_security_score = table.Column<int>(type: "integer", nullable: false),
                    min_reliability_score = table.Column<int>(type: "integer", nullable: false),
                    max_code_smells = table.Column<int>(type: "integer", nullable: false),
                    max_vulnerabilities = table.Column<int>(type: "integer", nullable: false),
                    max_security_hotspots = table.Column<int>(type: "integer", nullable: false),
                    max_bugs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quality_gate_config", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rule_suppressions",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    rule_key = table.Column<string>(type: "character varying(500)", nullable: false),
                    suppressed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rule_suppressions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sbom_components",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ingestion_id = table.Column<string>(type: "text", nullable: false),
                    analysis_id = table.Column<string>(type: "text", nullable: false),
                    report_id = table.Column<string>(type: "text", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: true),
                    purl = table.Column<string>(type: "text", nullable: true),
                    ecosystem = table.Column<string>(type: "text", nullable: true),
                    licenses = table.Column<string>(type: "text", nullable: false),
                    relationship = table.Column<string>(type: "text", nullable: false),
                    license_decision = table.Column<string>(type: "text", nullable: false),
                    vulnerabilities = table.Column<string>(type: "text", nullable: false),
                    dependencies = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    observed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sbom_components", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ingestion_reports",
                columns: table => new
                {
                    ingestion_id = table.Column<string>(type: "text", nullable: false),
                    report_id = table.Column<string>(type: "text", nullable: false),
                    blob_name = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingestion_reports", x => new { x.ingestion_id, x.report_id });
                    table.ForeignKey(
                        name: "FK_ingestion_reports_ingestions_ingestion_id",
                        column: x => x.ingestion_id,
                        principalTable: "ingestions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    archived = table.Column<bool>(type: "boolean", nullable: false),
                    organization_id = table.Column<string>(type: "character varying(36)", nullable: true),
                    analysis_profile_id = table.Column<string>(type: "character varying(100)", nullable: true),
                    analysis_profile_version = table.Column<string>(type: "character varying(50)", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                    table.ForeignKey(
                        name: "FK_projects_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "token_project_access",
                columns: table => new
                {
                    token_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    project_key = table.Column<string>(type: "character varying(100)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_token_project_access", x => new { x.token_id, x.project_key });
                    table.ForeignKey(
                        name: "FK_token_project_access_platform_api_tokens_token_id",
                        column: x => x.token_id,
                        principalTable: "platform_api_tokens",
                        principalColumn: "token_id");
                });

            migrationBuilder.CreateTable(
                name: "user_grants",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    user_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    scope_type = table.Column<string>(type: "character varying(20)", nullable: false),
                    scope_id = table.Column<string>(type: "character varying(100)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_grants_platform_users_user_id",
                        column: x => x.user_id,
                        principalTable: "platform_users",
                        principalColumn: "user_id");
                });

            migrationBuilder.CreateTable(
                name: "project_gate_assignments",
                columns: table => new
                {
                    project_key = table.Column<string>(type: "character varying(255)", nullable: false),
                    gate_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    assigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_gate_assignments", x => x.project_key);
                    table.ForeignKey(
                        name: "FK_project_gate_assignments_quality_gate_config_gate_id",
                        column: x => x.gate_id,
                        principalTable: "quality_gate_config",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "branches",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branches", x => x.id);
                    table.ForeignKey(
                        name: "FK_branches_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "retention_policies",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", nullable: false),
                    project_id = table.Column<string>(type: "character varying(36)", nullable: false),
                    keep_analyses_days = table.Column<int>(type: "integer", nullable: false),
                    keep_failed_analyses_days = table.Column<int>(type: "integer", nullable: false),
                    keep_master_branch_days = table.Column<int>(type: "integer", nullable: false),
                    auto_delete_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_cleanup_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_policies", x => x.id);
                    table.ForeignKey(
                        name: "FK_retention_policies_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_analysis_records_branch",
                table: "analysis_records",
                column: "branch");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_records_commit_sha",
                table: "analysis_records",
                column: "commit_sha");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_records_created_at",
                table: "analysis_records",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_records_project_key",
                table: "analysis_records",
                column: "project_key");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_records_status",
                table: "analysis_records",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_action",
                table: "audit_logs",
                column: "action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_actor_email",
                table: "audit_logs",
                column: "actor_email");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_created_at",
                table: "audit_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_azure_devops_org_config_config_name",
                table: "azure_devops_org_config",
                column: "config_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_azure_devops_org_config_organization_url",
                table: "azure_devops_org_config",
                column: "organization_url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_branches_project_id",
                table: "branches",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_branches_project_id_is_default",
                table: "branches",
                columns: new[] { "project_id", "is_default" });

            migrationBuilder.CreateIndex(
                name: "IX_branches_project_id_name",
                table: "branches",
                columns: new[] { "project_id", "name" });

            migrationBuilder.CreateIndex(
                name: "IX_collaboration_teams_project_key_name",
                table: "collaboration_teams",
                columns: new[] { "project_key", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_findings_assignee_user_id_team_id",
                table: "durable_findings",
                columns: new[] { "assignee_user_id", "team_id" });

            migrationBuilder.CreateIndex(
                name: "IX_durable_findings_project_key_branch_id",
                table: "durable_findings",
                columns: new[] { "project_key", "branch", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_kind_resource_id",
                table: "durable_jobs",
                columns: new[] { "kind", "resource_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_state_available_at",
                table: "durable_jobs",
                columns: new[] { "state", "available_at" });

            migrationBuilder.CreateIndex(
                name: "IX_finding_decisions_finding_id_created_at_id",
                table: "finding_decisions",
                columns: new[] { "finding_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_finding_identity_matches_finding_id_created_at",
                table: "finding_identity_matches",
                columns: new[] { "finding_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_finding_identity_matches_project_key_branch_occurrence_iden~",
                table: "finding_identity_matches",
                columns: new[] { "project_key", "branch", "occurrence_identity", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_finding_occurrences_analysis_id_tool_rule_id",
                table: "finding_occurrences",
                columns: new[] { "analysis_id", "tool", "rule_id" });

            migrationBuilder.CreateIndex(
                name: "IX_finding_occurrences_ingestion_id_id",
                table: "finding_occurrences",
                columns: new[] { "ingestion_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_ingestions_analysis_id_revision",
                table: "ingestions",
                columns: new[] { "analysis_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ingestions_build_key_manifest_hash",
                table: "ingestions",
                columns: new[] { "build_key", "manifest_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ingestions_project_key_created_at",
                table: "ingestions",
                columns: new[] { "project_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_oidc_transactions_cookie_hash",
                table: "oidc_transactions",
                column: "cookie_hash");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_key",
                table: "organizations",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_api_tokens_is_current_revoked",
                table: "platform_api_tokens",
                columns: new[] { "is_current", "revoked" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_api_tokens_token_hash",
                table: "platform_api_tokens",
                column: "token_hash");

            migrationBuilder.CreateIndex(
                name: "IX_platform_settings_key",
                table: "platform_settings",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_email",
                table: "platform_users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_entra_object_id",
                table: "platform_users",
                column: "entra_object_id");

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_entra_tenant_id",
                table: "platform_users",
                column: "entra_tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_entra_tenant_id_entra_object_id",
                table: "platform_users",
                columns: new[] { "entra_tenant_id", "entra_object_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_role",
                table: "platform_users",
                column: "role");

            migrationBuilder.CreateIndex(
                name: "IX_profile_exceptions_project_key_expires_at",
                table: "profile_exceptions",
                columns: new[] { "project_key", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_project_gate_assignments_gate_id",
                table: "project_gate_assignments",
                column: "gate_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_integrations_project_key",
                table: "project_integrations",
                column: "project_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_archived",
                table: "projects",
                column: "archived");

            migrationBuilder.CreateIndex(
                name: "IX_projects_key",
                table: "projects",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_organization_id",
                table: "projects",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_retention_policies_project_id",
                table: "retention_policies",
                column: "project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rule_suppressions_rule_key",
                table: "rule_suppressions",
                column: "rule_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sbom_components_ingestion_id_id",
                table: "sbom_components",
                columns: new[] { "ingestion_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_token_project_access_project_key",
                table: "token_project_access",
                column: "project_key");

            migrationBuilder.CreateIndex(
                name: "IX_user_grants_scope_id",
                table: "user_grants",
                column: "scope_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_grants_user_id",
                table: "user_grants",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_grants_user_id_scope_type_scope_id",
                table: "user_grants",
                columns: new[] { "user_id", "scope_type", "scope_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analysis_records");

            migrationBuilder.DropTable(
                name: "archived_code_branches");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "auth_provider_config");

            migrationBuilder.DropTable(
                name: "azure_devops_org_config");

            migrationBuilder.DropTable(
                name: "branches");

            migrationBuilder.DropTable(
                name: "code_scope_assignments");

            migrationBuilder.DropTable(
                name: "code_scope_versions");

            migrationBuilder.DropTable(
                name: "collaboration_teams");

            migrationBuilder.DropTable(
                name: "coverage_lines");

            migrationBuilder.DropTable(
                name: "durable_findings");

            migrationBuilder.DropTable(
                name: "durable_jobs");

            migrationBuilder.DropTable(
                name: "finding_decisions");

            migrationBuilder.DropTable(
                name: "finding_identity_matches");

            migrationBuilder.DropTable(
                name: "finding_occurrences");

            migrationBuilder.DropTable(
                name: "ingestion_project_policies");

            migrationBuilder.DropTable(
                name: "ingestion_reports");

            migrationBuilder.DropTable(
                name: "notification_channels");

            migrationBuilder.DropTable(
                name: "oidc_transactions");

            migrationBuilder.DropTable(
                name: "platform_settings");

            migrationBuilder.DropTable(
                name: "profile_exceptions");

            migrationBuilder.DropTable(
                name: "profile_versions");

            migrationBuilder.DropTable(
                name: "project_gate_assignments");

            migrationBuilder.DropTable(
                name: "project_integrations");

            migrationBuilder.DropTable(
                name: "project_ownerships");

            migrationBuilder.DropTable(
                name: "retention_policies");

            migrationBuilder.DropTable(
                name: "rule_suppressions");

            migrationBuilder.DropTable(
                name: "sbom_components");

            migrationBuilder.DropTable(
                name: "token_project_access");

            migrationBuilder.DropTable(
                name: "user_grants");

            migrationBuilder.DropTable(
                name: "ingestions");

            migrationBuilder.DropTable(
                name: "quality_gate_config");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "platform_api_tokens");

            migrationBuilder.DropTable(
                name: "platform_users");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
