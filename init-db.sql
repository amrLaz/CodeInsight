CREATE TABLE IF NOT EXISTS analysis_records (
    analysis_id VARCHAR(255) PRIMARY KEY,
    project_key VARCHAR(255) NOT NULL,
    commit_sha VARCHAR(255),
    branch VARCHAR(255),
    ci_provider VARCHAR(50),
    status VARCHAR(20),
    quality_gate VARCHAR(20),
    maintainability_score INTEGER,
    security_score INTEGER,
    reliability_score INTEGER,
    code_smells INTEGER,
    vulnerabilities INTEGER,
    security_hotspots INTEGER,
    bugs INTEGER,
    coverage INTEGER DEFAULT 0,
    duplication INTEGER DEFAULT 0,
    affected_files TEXT,
    gate_failures TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    pr_id VARCHAR(255),
    pr_repo_id VARCHAR(255),
    pr_ado_project VARCHAR(255),
    pr_target_branch VARCHAR(255),
    pr_decoration_status VARCHAR(50),
    pr_decoration_error TEXT,
    project_id UUID,
    branch_id UUID
);

CREATE TABLE IF NOT EXISTS quality_gate_configs (
    id VARCHAR(255) PRIMARY KEY,
    name VARCHAR(255),
    gates TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS azure_devops_configs (
    id VARCHAR(255) PRIMARY KEY,
    organization VARCHAR(255),
    project VARCHAR(255),
    pat VARCHAR(500),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_analysis_records_project_key ON analysis_records(project_key);
CREATE INDEX IF NOT EXISTS idx_analysis_records_created_at ON analysis_records(created_at);
