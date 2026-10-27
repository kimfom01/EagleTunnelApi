using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EagleTunnelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_key = table.Column<string>(type: "text", nullable: false),
                    to_email = table.Column<string>(type: "text", nullable: false),
                    payload_protected = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    dedupe_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_outbox", x => x.id);
                    table.CheckConstraint("ck_email_outbox_attempts_non_negative", "attempts >= 0");
                    table.CheckConstraint("ck_email_outbox_status", "status IN ('pending', 'processing', 'sent', 'dead')");
                });

            migrationBuilder.CreateTable(
                name: "panel_clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    panel_client_id = table.Column<long>(type: "bigint", nullable: false),
                    email_normalized = table.Column<string>(type: "text", nullable: false),
                    telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    enable = table.Column<bool>(type: "boolean", nullable: false),
                    expiry_time = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    total_gb = table.Column<long>(type: "bigint", nullable: false),
                    limit_ip = table.Column<int>(type: "integer", nullable: false),
                    limit_hwid = table.Column<int>(type: "integer", nullable: false),
                    traffic_reset = table.Column<string>(type: "text", nullable: true),
                    traffic_reset_day = table.Column<int>(type: "integer", nullable: true),
                    reset = table.Column<int>(type: "integer", nullable: false),
                    sub_id = table.Column<string>(type: "text", nullable: true),
                    flow = table.Column<string>(type: "text", nullable: true),
                    security = table.Column<string>(type: "text", nullable: true),
                    traffic_up = table.Column<long>(type: "bigint", nullable: false),
                    traffic_down = table.Column<long>(type: "bigint", nullable: false),
                    traffic_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    legacy_comment = table.Column<string>(type: "text", nullable: true),
                    panel_created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    panel_updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_panel_clients", x => x.id);
                    table.UniqueConstraint("AK_panel_clients_panel_client_id", x => x.panel_client_id);
                });

            migrationBuilder.CreateTable(
                name: "panel_inbounds",
                columns: table => new
                {
                    inbound_id = table.Column<int>(type: "integer", nullable: false),
                    remark = table.Column<string>(type: "text", nullable: true),
                    tag = table.Column<string>(type: "text", nullable: true),
                    protocol = table.Column<string>(type: "text", nullable: true),
                    port = table.Column<int>(type: "integer", nullable: true),
                    is_default_for_new_clients = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_panel_inbounds", x => x.inbound_id);
                });

            migrationBuilder.CreateTable(
                name: "panel_sync_state",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    cursor_panel_updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_run_mode = table.Column<string>(type: "text", nullable: true),
                    last_started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    last_seen_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_changed_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    lock_owner = table.Column<string>(type: "text", nullable: true),
                    lock_expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_panel_sync_state", x => x.id);
                    table.CheckConstraint("ck_panel_sync_state_counts_non_negative", "last_seen_count >= 0 AND last_changed_count >= 0");
                    table.CheckConstraint("ck_panel_sync_state_last_run_mode", "last_run_mode IS NULL OR last_run_mode IN ('cursor', 'full')");
                });

            migrationBuilder.CreateTable(
                name: "app_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_normalized = table.Column<string>(type: "text", nullable: false),
                    telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    panel_client_id = table.Column<long>(type: "bigint", nullable: false),
                    email_verified_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_users", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_users_panel_clients_panel_client_id",
                        column: x => x.panel_client_id,
                        principalTable: "panel_clients",
                        principalColumn: "panel_client_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "panel_client_inbounds",
                columns: table => new
                {
                    panel_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inbound_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_panel_client_inbounds", x => new { x.panel_client_id, x.inbound_id });
                    table.ForeignKey(
                        name: "FK_panel_client_inbounds_panel_clients_panel_client_id",
                        column: x => x.panel_client_id,
                        principalTable: "panel_clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_panel_client_inbounds_panel_inbounds_inbound_id",
                        column: x => x.inbound_id,
                        principalTable: "panel_inbounds",
                        principalColumn: "inbound_id");
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    email_normalized = table.Column<string>(type: "text", nullable: true),
                    telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_events_app_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "cancellation_state",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cancellation_state", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_cancellation_state_app_users_user_id",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "credit_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    reference_type = table.Column<string>(type: "text", nullable: true),
                    reference_id = table.Column<string>(type: "text", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    metadata = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_entries", x => x.id);
                    table.CheckConstraint("ck_credit_entries_amount_non_zero", "amount <> 0");
                    table.CheckConstraint("ck_credit_entries_currency", "(unit = 'currency' AND currency IS NOT NULL) OR (unit = 'days' AND currency IS NULL)");
                    table.CheckConstraint("ck_credit_entries_unit", "unit IN ('days', 'currency')");
                    table.ForeignKey(
                        name: "FK_credit_entries_app_users_user_id",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "email_login_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    panel_client_id = table.Column<long>(type: "bigint", nullable: false),
                    app_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    email_normalized = table.Column<string>(type: "text", nullable: false),
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    request_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_login_codes", x => x.id);
                    table.CheckConstraint("ck_email_login_codes_attempts_non_negative", "attempts >= 0");
                    table.ForeignKey(
                        name: "FK_email_login_codes_app_users_app_user_id",
                        column: x => x.app_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_email_login_codes_panel_clients_panel_client_id",
                        column: x => x.panel_client_id,
                        principalTable: "panel_clients",
                        principalColumn: "panel_client_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "panel_sync_conflicts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dedupe_key = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    email_normalized = table.Column<string>(type: "text", nullable: true),
                    telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    panel_client_id = table.Column<long>(type: "bigint", nullable: true),
                    other_panel_client_id = table.Column<long>(type: "bigint", nullable: true),
                    app_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "open"),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    resolved_by = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_panel_sync_conflicts", x => x.id);
                    table.CheckConstraint("ck_panel_sync_conflicts_status", "status IN ('open', 'resolved', 'dismissed')");
                    table.ForeignKey(
                        name: "FK_panel_sync_conflicts_app_users_app_user_id",
                        column: x => x.app_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "referral_attributions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    referrer_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    referee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    referrer_email = table.Column<string>(type: "text", nullable: true),
                    referrer_telegram_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_referral_attributions", x => x.id);
                    table.CheckConstraint("ck_referral_attributions_source", "source IN ('telegram', 'admin', 'migration')");
                    table.CheckConstraint("ck_referral_attributions_status", "status IN ('pending', 'paid', 'void')");
                    table.ForeignKey(
                        name: "FK_referral_attributions_app_users_referee_user_id",
                        column: x => x.referee_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_referral_attributions_app_users_referrer_user_id",
                        column: x => x.referrer_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reminder_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    days_before = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_deliveries", x => x.id);
                    table.CheckConstraint("ck_reminder_deliveries_days_before_non_negative", "days_before >= 0");
                    table.ForeignKey(
                        name: "FK_reminder_deliveries_app_users_user_id",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "trial_periods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trial_periods", x => x.id);
                    table.CheckConstraint("ck_trial_periods_period_valid", "ends_at > starts_at");
                    table.ForeignKey(
                        name: "FK_trial_periods_app_users_user_id",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "web_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_web_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_web_sessions_app_users_app_user_id",
                        column: x => x.app_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "credit_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    panel_expiry_before = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    panel_expiry_after = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credit_applications", x => x.id);
                    table.ForeignKey(
                        name: "FK_credit_applications_app_users_user_id",
                        column: x => x.user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credit_applications_credit_entries_entry_id",
                        column: x => x.entry_id,
                        principalTable: "credit_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_app_users_email_normalized",
                table: "app_users",
                column: "email_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_app_users_panel_client_id",
                table: "app_users",
                column: "panel_client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_app_users_telegram_id",
                table: "app_users",
                column: "telegram_id",
                unique: true,
                filter: "telegram_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor_user_id_occurred_at",
                table: "audit_events",
                columns: new[] { "actor_user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_event_type_occurred_at",
                table: "audit_events",
                columns: new[] { "event_type", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred_at",
                table: "audit_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_credit_applications_user_id",
                table: "credit_applications",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_credit_applications_entry_id",
                table: "credit_applications",
                column: "entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_credit_entries_user_id_unit_occurred_at",
                table: "credit_entries",
                columns: new[] { "user_id", "unit", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ux_credit_entries_idempotency_key",
                table: "credit_entries",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_login_codes_app_user_id",
                table: "email_login_codes",
                column: "app_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_login_codes_email_normalized_expires_at",
                table: "email_login_codes",
                columns: new[] { "email_normalized", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_email_login_codes_panel_client_id_expires_at",
                table: "email_login_codes",
                columns: new[] { "panel_client_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_created_at",
                table: "email_outbox",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_status_next_attempt_at",
                table: "email_outbox",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ux_email_outbox_pending_dedupe_key",
                table: "email_outbox",
                column: "dedupe_key",
                unique: true,
                filter: "dedupe_key IS NOT NULL AND status IN ('pending', 'processing')");

            migrationBuilder.CreateIndex(
                name: "IX_panel_client_inbounds_inbound_id",
                table: "panel_client_inbounds",
                column: "inbound_id");

            migrationBuilder.CreateIndex(
                name: "ix_panel_clients_is_deleted_last_synced_at",
                table: "panel_clients",
                columns: new[] { "is_deleted", "last_synced_at" });

            migrationBuilder.CreateIndex(
                name: "ix_panel_clients_panel_updated_at",
                table: "panel_clients",
                column: "panel_updated_at");

            migrationBuilder.CreateIndex(
                name: "ux_panel_clients_email_normalized",
                table: "panel_clients",
                column: "email_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_panel_clients_telegram_id",
                table: "panel_clients",
                column: "telegram_id",
                unique: true,
                filter: "telegram_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_panel_sync_conflicts_app_user_id",
                table: "panel_sync_conflicts",
                column: "app_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_panel_sync_conflicts_status_last_seen_at",
                table: "panel_sync_conflicts",
                columns: new[] { "status", "last_seen_at" });

            migrationBuilder.CreateIndex(
                name: "ux_panel_sync_conflicts_dedupe_key",
                table: "panel_sync_conflicts",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_referral_attributions_referee_user_id",
                table: "referral_attributions",
                column: "referee_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_referral_attributions_referrer_user_id",
                table: "referral_attributions",
                column: "referrer_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_referral_attributions_idempotency_key",
                table: "referral_attributions",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_reminder_deliveries_user_expiry_days",
                table: "reminder_deliveries",
                columns: new[] { "user_id", "expiry_date", "days_before" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trial_periods_user_id",
                table: "trial_periods",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_web_sessions_app_user_id_expires_at",
                table: "web_sessions",
                columns: new[] { "app_user_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ux_web_sessions_token_hash",
                table: "web_sessions",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "cancellation_state");

            migrationBuilder.DropTable(
                name: "credit_applications");

            migrationBuilder.DropTable(
                name: "email_login_codes");

            migrationBuilder.DropTable(
                name: "email_outbox");

            migrationBuilder.DropTable(
                name: "panel_client_inbounds");

            migrationBuilder.DropTable(
                name: "panel_sync_conflicts");

            migrationBuilder.DropTable(
                name: "panel_sync_state");

            migrationBuilder.DropTable(
                name: "referral_attributions");

            migrationBuilder.DropTable(
                name: "reminder_deliveries");

            migrationBuilder.DropTable(
                name: "trial_periods");

            migrationBuilder.DropTable(
                name: "web_sessions");

            migrationBuilder.DropTable(
                name: "credit_entries");

            migrationBuilder.DropTable(
                name: "panel_inbounds");

            migrationBuilder.DropTable(
                name: "app_users");

            migrationBuilder.DropTable(
                name: "panel_clients");
        }
    }
}
