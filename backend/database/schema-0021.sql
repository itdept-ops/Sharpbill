-- Sharpbill schema snapshot at the final Python/Alembic revision (0021).
--
-- This file is intentionally append-only evidence. It is used only when the selected
-- database has zero base tables. Existing databases are validated, never altered from
-- this snapshot. Keep the historical Alembic revisions in ../alembic/versions intact.

CREATE TABLE `alembic_version` (
  `version_num` varchar(32) NOT NULL,
  PRIMARY KEY (`version_num`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `permissions` (
  `id` int NOT NULL AUTO_INCREMENT,
  `key` varchar(100) NOT NULL,
  `description` varchar(255) DEFAULT NULL,
  `is_system` tinyint(1) NOT NULL DEFAULT 0,
  `created_at` datetime(6) NOT NULL DEFAULT (now(6)),
  `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_permissions_key` (`key`),
  CONSTRAINT `ck_permissions_is_system_boolean` CHECK (`is_system` IN (0, 1))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `roles` (
  `id` int NOT NULL AUTO_INCREMENT,
  `name` varchar(50) NOT NULL,
  `description` varchar(255) DEFAULT NULL,
  `is_system` tinyint(1) NOT NULL DEFAULT 0,
  `created_at` datetime(6) NOT NULL DEFAULT (now(6)),
  `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  `version` int NOT NULL DEFAULT 1,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_roles_name` (`name`),
  CONSTRAINT `ck_roles_is_system_boolean` CHECK (`is_system` IN (0, 1))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `role_permissions` (
  `role_id` int NOT NULL,
  `permission_id` int NOT NULL,
  PRIMARY KEY (`role_id`, `permission_id`),
  KEY `fk_role_permissions_permission_id_permissions` (`permission_id`),
  CONSTRAINT `fk_role_permissions_permission_id_permissions`
    FOREIGN KEY (`permission_id`) REFERENCES `permissions` (`id`) ON DELETE CASCADE,
  CONSTRAINT `fk_role_permissions_role_id_roles`
    FOREIGN KEY (`role_id`) REFERENCES `roles` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `users` (
  `id` int NOT NULL AUTO_INCREMENT,
  `email` varchar(255) NOT NULL,
  `display_name` varchar(255) DEFAULT NULL,
  `is_active` tinyint(1) NOT NULL DEFAULT 1,
  `last_login_at` datetime(6) DEFAULT NULL,
  `created_at` datetime(6) NOT NULL DEFAULT (now(6)),
  `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  `role_id` int NOT NULL,
  `last_seen_at` datetime(6) DEFAULT NULL,
  `session_valid_after` datetime(6) DEFAULT NULL,
  `title` varchar(120) DEFAULT NULL,
  `department` varchar(120) DEFAULT NULL,
  `phone` varchar(40) DEFAULT NULL,
  `location` varchar(120) DEFAULT NULL,
  `timezone` varchar(60) DEFAULT NULL,
  `bio` varchar(500) DEFAULT NULL,
  `is_approved` tinyint(1) NOT NULL DEFAULT 1,
  `last_latitude` double DEFAULT NULL,
  `last_longitude` double DEFAULT NULL,
  `last_location_accuracy` double DEFAULT NULL,
  `last_location_at` datetime(6) DEFAULT NULL,
  `accent_color` varchar(9) DEFAULT NULL,
  `ui_prefs` json DEFAULT NULL,
  `access_version` int NOT NULL DEFAULT 1,
  `deactivated_at` datetime(6) DEFAULT NULL,
  `erasure_requested_at` datetime(6) DEFAULT NULL,
  `erasure_due_at` datetime(6) DEFAULT NULL,
  `erased_at` datetime(6) DEFAULT NULL,
  `location_retention_until` datetime(6) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_users_email` (`email`),
  KEY `ix_users_role_id` (`role_id`),
  KEY `ix_users_last_seen_at` (`last_seen_at`),
  KEY `ix_users_created_at_id` (`created_at`, `id`),
  KEY `ix_users_last_location_at_id` (`last_location_at`, `id`),
  KEY `ix_users_deactivated_at_id` (`deactivated_at`, `id`),
  KEY `ix_users_erasure_due_at_id` (`erasure_due_at`, `id`),
  KEY `ix_users_location_retention_until_id` (`location_retention_until`, `id`),
  CONSTRAINT `fk_users_role_id_roles`
    FOREIGN KEY (`role_id`) REFERENCES `roles` (`id`) ON DELETE RESTRICT,
  CONSTRAINT `ck_users_deactivation_state_valid` CHECK (
    (`is_active` = 1 AND `deactivated_at` IS NULL)
    OR (`is_active` = 0 AND `deactivated_at` IS NOT NULL)
  ),
  CONSTRAINT `ck_users_erasure_schedule_valid` CHECK (
    (`erasure_requested_at` IS NULL AND `erasure_due_at` IS NULL)
    OR (`erasure_requested_at` IS NOT NULL AND `erasure_due_at` IS NOT NULL
        AND `erasure_due_at` >= `erasure_requested_at`)
  ),
  CONSTRAINT `ck_users_erasure_state_valid` CHECK (
    `erased_at` IS NULL
    OR (`is_active` = 0 AND `is_approved` = 0 AND `deactivated_at` IS NOT NULL
        AND `erased_at` >= `deactivated_at`
        AND (`erasure_requested_at` IS NULL OR `erased_at` >= `erasure_requested_at`))
  ),
  CONSTRAINT `ck_users_is_active_boolean` CHECK (`is_active` IN (0, 1)),
  CONSTRAINT `ck_users_is_approved_boolean` CHECK (`is_approved` IN (0, 1)),
  CONSTRAINT `ck_users_last_latitude_valid` CHECK (
    `last_latitude` IS NULL OR `last_latitude` BETWEEN -90 AND 90
  ),
  CONSTRAINT `ck_users_last_location_accuracy_valid` CHECK (
    `last_location_accuracy` IS NULL OR `last_location_accuracy` BETWEEN 0 AND 100000
  ),
  CONSTRAINT `ck_users_last_longitude_valid` CHECK (
    `last_longitude` IS NULL OR `last_longitude` BETWEEN -180 AND 180
  ),
  CONSTRAINT `ck_users_location_retention_valid` CHECK (
    (`last_latitude` IS NULL AND `last_longitude` IS NULL
     AND `last_location_accuracy` IS NULL AND `last_location_at` IS NULL
     AND `location_retention_until` IS NULL)
    OR ((`last_latitude` IS NOT NULL OR `last_longitude` IS NOT NULL
         OR `last_location_accuracy` IS NOT NULL)
        AND `last_location_at` IS NOT NULL
        AND `location_retention_until` IS NOT NULL
        AND `location_retention_until` >= `last_location_at`)
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `user_identities` (
  `id` int NOT NULL AUTO_INCREMENT,
  `user_id` int NOT NULL,
  `provider` varchar(20) NOT NULL,
  `provider_subject` varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `created_at` datetime(6) NOT NULL DEFAULT (now(6)),
  `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  `provider_tenant_id` varchar(255) DEFAULT NULL,
  `provider_hosted_domain` varchar(255) DEFAULT NULL,
  `provider_namespace` varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL DEFAULT '',
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_user_identities_provider_namespace_subject`
    (`provider`, `provider_namespace`, `provider_subject`),
  KEY `ix_user_identities_user_id` (`user_id`),
  CONSTRAINT `fk_user_identities_user_id_users`
    FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `user_permissions` (
  `user_id` int NOT NULL,
  `permission_id` int NOT NULL,
  PRIMARY KEY (`user_id`, `permission_id`),
  KEY `fk_user_permissions_permission_id` (`permission_id`),
  CONSTRAINT `fk_user_permissions_permission_id`
    FOREIGN KEY (`permission_id`) REFERENCES `permissions` (`id`) ON DELETE CASCADE,
  CONSTRAINT `fk_user_permissions_user_id`
    FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `user_sessions` (
  `id` int NOT NULL AUTO_INCREMENT,
  `user_id` int NOT NULL,
  `jti` varchar(36) NOT NULL,
  `user_agent` varchar(400) DEFAULT NULL,
  `ip` varchar(45) DEFAULT NULL,
  `created_at` datetime(6) NOT NULL DEFAULT (now(6)),
  `last_seen_at` datetime(6) DEFAULT NULL,
  `revoked_at` datetime(6) DEFAULT NULL,
  `expires_at` datetime(6) NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_user_sessions_jti` (`jti`),
  KEY `ix_user_sessions_user_revoked_created` (`user_id`, `revoked_at`, `created_at`),
  KEY `ix_user_sessions_expires_at` (`expires_at`),
  KEY `ix_user_sessions_revoked_at` (`revoked_at`),
  CONSTRAINT `fk_user_sessions_user_id_users`
    FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `site_settings` (
  `id` int NOT NULL,
  `signup_mode` varchar(20) NOT NULL DEFAULT 'open',
  `allow_google` tinyint(1) NOT NULL DEFAULT 1,
  `allow_microsoft` tinyint(1) NOT NULL DEFAULT 1,
  `default_role_id` int NOT NULL,
  `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  `calm_mode` tinyint(1) NOT NULL DEFAULT 0,
  `retention_hold` tinyint(1) NOT NULL DEFAULT 0,
  `retention_hold_reference` varchar(255) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `fk_site_settings_default_role_id_roles` (`default_role_id`),
  CONSTRAINT `fk_site_settings_default_role_id_roles`
    FOREIGN KEY (`default_role_id`) REFERENCES `roles` (`id`) ON DELETE RESTRICT,
  CONSTRAINT `ck_site_settings_allow_google_boolean` CHECK (`allow_google` IN (0, 1)),
  CONSTRAINT `ck_site_settings_allow_microsoft_boolean` CHECK (`allow_microsoft` IN (0, 1)),
  CONSTRAINT `ck_site_settings_calm_mode_boolean` CHECK (`calm_mode` IN (0, 1)),
  CONSTRAINT `ck_site_settings_provider_available` CHECK (
    `allow_google` = 1 OR `allow_microsoft` = 1
  ),
  CONSTRAINT `ck_site_settings_retention_hold_boolean` CHECK (`retention_hold` IN (0, 1)),
  CONSTRAINT `ck_site_settings_retention_hold_state_valid` CHECK (
    (`retention_hold` = 0 AND `retention_hold_reference` IS NULL)
    OR (`retention_hold` = 1 AND `retention_hold_reference` IS NOT NULL
        AND CHAR_LENGTH(TRIM(`retention_hold_reference`)) BETWEEN 1 AND 255)
  ),
  CONSTRAINT `ck_site_settings_signup_mode_valid` CHECK (
    `signup_mode` IN ('open', 'approval', 'closed')
  ),
  CONSTRAINT `ck_site_settings_singleton_id` CHECK (`id` = 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `login_nonces` (
  `nonce` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `created_at` datetime(6) NOT NULL DEFAULT (now(6)),
  `expires_at` datetime(6) NOT NULL,
  PRIMARY KEY (`nonce`),
  KEY `ix_login_nonces_expires_at` (`expires_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `request_logs` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `method` varchar(10) NOT NULL,
  `path` varchar(255) NOT NULL,
  `user_id` int DEFAULT NULL,
  `ip` varchar(45) DEFAULT NULL,
  `status_code` int NOT NULL,
  `created_at` datetime(6) NOT NULL DEFAULT (now(6)),
  PRIMARY KEY (`id`),
  KEY `ix_request_logs_created_at` (`created_at`),
  KEY `ix_request_logs_user_id_id` (`user_id`, `id`),
  KEY `ix_request_logs_method_id` (`method`, `id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `security_events` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `event_type` varchar(80) NOT NULL,
  `outcome` varchar(16) NOT NULL,
  `severity` varchar(16) NOT NULL,
  `request_id` varchar(64) DEFAULT NULL,
  `actor_user_id` int DEFAULT NULL,
  `target_type` varchar(40) DEFAULT NULL,
  `target_id` varchar(128) DEFAULT NULL,
  `source_ip` varchar(45) DEFAULT NULL,
  `metadata` json NOT NULL,
  `occurred_at` datetime(6) NOT NULL DEFAULT (now(6)),
  `retention_until` datetime(6) NOT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_security_events_occurred_id` (`occurred_at`, `id`),
  KEY `ix_security_events_type_id` (`event_type`, `id`),
  KEY `ix_security_events_actor_id` (`actor_user_id`, `id`),
  KEY `ix_security_events_request_id` (`request_id`),
  KEY `ix_security_events_retention_until` (`retention_until`),
  CONSTRAINT `ck_security_events_outcome_valid` CHECK (
    `outcome` IN ('success', 'failure', 'denied')
  ),
  CONSTRAINT `ck_security_events_severity_valid` CHECK (
    `severity` IN ('info', 'warning', 'critical')
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `security_event_deliveries` (
  `event_id` bigint NOT NULL,
  `status` varchar(16) NOT NULL DEFAULT 'pending',
  `attempts` int NOT NULL DEFAULT 0,
  `next_attempt_at` datetime(6) NOT NULL DEFAULT (now(6)),
  `lease_owner` varchar(64) DEFAULT NULL,
  `lease_expires_at` datetime(6) DEFAULT NULL,
  `last_attempt_at` datetime(6) DEFAULT NULL,
  `delivered_at` datetime(6) DEFAULT NULL,
  `last_error` varchar(255) DEFAULT NULL,
  `updated_at` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`event_id`),
  KEY `ix_security_event_deliveries_dispatch` (`status`, `next_attempt_at`, `event_id`),
  KEY `ix_security_event_deliveries_lease` (`lease_expires_at`),
  CONSTRAINT `fk_security_event_deliveries_event_id_security_events`
    FOREIGN KEY (`event_id`) REFERENCES `security_events` (`id`) ON DELETE CASCADE,
  CONSTRAINT `ck_security_event_deliveries_attempts_nonnegative` CHECK (`attempts` >= 0),
  CONSTRAINT `ck_security_event_deliveries_status_valid` CHECK (
    `status` IN ('pending', 'leased', 'retry', 'delivered', 'dead_letter')
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `legal_acceptances` (
  `id` bigint NOT NULL AUTO_INCREMENT,
  `user_id` int NOT NULL,
  `bundle_version` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `terms_version` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `eula_version` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `acceptable_use_version` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `privacy_version` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `terms_sha256` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `eula_sha256` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `acceptable_use_sha256` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `privacy_sha256` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `accepted_at` datetime(6) NOT NULL,
  `retention_until` datetime(6) NOT NULL,
  `source_ip` varchar(45) DEFAULT NULL,
  `user_agent` varchar(400) DEFAULT NULL,
  `request_id` varchar(64) DEFAULT NULL,
  `personal_data_erased_at` datetime(6) DEFAULT NULL,
  `bundle_effective_date` date NOT NULL,
  `acceptance_label` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `terms_action` varchar(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `eula_action` varchar(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `acceptable_use_action` varchar(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  `privacy_action` varchar(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
  PRIMARY KEY (`id`),
  KEY `ix_legal_acceptances_user_accepted_id` (`user_id`, `accepted_at`, `id`),
  KEY `ix_legal_acceptances_accepted_id` (`accepted_at`, `id`),
  KEY `ix_legal_acceptances_retention_id` (`retention_until`, `id`),
  CONSTRAINT `fk_legal_acceptances_user_id_users`
    FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT,
  CONSTRAINT `ck_legal_acceptances_terms_sha256_valid` CHECK (
    `terms_sha256` REGEXP '^[0-9a-f]{64}$'
  ),
  CONSTRAINT `ck_legal_acceptances_eula_sha256_valid` CHECK (
    `eula_sha256` REGEXP '^[0-9a-f]{64}$'
  ),
  CONSTRAINT `ck_legal_acceptances_acceptable_use_sha256_valid` CHECK (
    `acceptable_use_sha256` REGEXP '^[0-9a-f]{64}$'
  ),
  CONSTRAINT `ck_legal_acceptances_privacy_sha256_valid` CHECK (
    `privacy_sha256` REGEXP '^[0-9a-f]{64}$'
  ),
  CONSTRAINT `ck_legal_acceptances_retention_after_acceptance` CHECK (
    `retention_until` > `accepted_at`
  ),
  CONSTRAINT `ck_legal_acceptances_personal_data_erasure_valid` CHECK (
    `personal_data_erased_at` IS NULL
    OR (`source_ip` IS NULL AND `user_agent` IS NULL AND `request_id` IS NULL
        AND `personal_data_erased_at` >= `accepted_at`)
  ),
  CONSTRAINT `ck_legal_acceptances_acceptance_label_valid` CHECK (
    CHAR_LENGTH(TRIM(`acceptance_label`)) BETWEEN 1 AND 500
  ),
  CONSTRAINT `ck_legal_acceptances_terms_action_valid` CHECK (
    `terms_action` IN ('agreement', 'acknowledgement')
  ),
  CONSTRAINT `ck_legal_acceptances_eula_action_valid` CHECK (
    `eula_action` IN ('agreement', 'acknowledgement')
  ),
  CONSTRAINT `ck_legal_acceptances_acceptable_use_action_valid` CHECK (
    `acceptable_use_action` IN ('agreement', 'acknowledgement')
  ),
  CONSTRAINT `ck_legal_acceptances_privacy_action_valid` CHECK (
    `privacy_action` IN ('agreement', 'acknowledgement')
  ),
  CONSTRAINT `ck_legal_acceptances_effective_date_not_after_acceptance` CHECK (
    `bundle_effective_date` <= DATE(`accepted_at`)
  )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO `permissions` (`id`, `key`, `description`, `is_system`) VALUES
  (1, 'users.read', 'View the user directory', 1),
  (2, 'users.manage', 'Manage user profiles, activation, and approval', 1),
  (3, 'roles.manage', 'Create and edit roles and permissions', 1),
  (4, 'presence.view', 'See who is currently online', 1),
  (5, 'presence.kick', 'Force sign-out (kick) a user''s active sessions', 1),
  (6, 'settings.manage', 'Manage site-wide configuration', 1),
  (9, 'logs.view', 'View the request activity log', 1),
  (10, 'users.export', 'Export the user directory as CSV', 1),
  (11, 'security_events.view', 'View and export durable security events', 1),
  (12, 'privacy.manage', 'Manage privacy requests, retention, and legal holds', 1);

INSERT INTO `roles` (`id`, `name`, `description`, `is_system`, `version`) VALUES
  (1, 'admin', 'Full access to every feature.', 1, 1),
  (2, 'user', 'Standard access for new members.', 1, 1);

INSERT INTO `role_permissions` (`role_id`, `permission_id`) VALUES
  (1, 1), (1, 2), (1, 3), (1, 4), (1, 5), (1, 6), (1, 9),
  (1, 10), (1, 11), (1, 12), (2, 4);

INSERT INTO `site_settings`
  (`id`, `signup_mode`, `allow_google`, `allow_microsoft`, `default_role_id`, `calm_mode`,
   `retention_hold`, `retention_hold_reference`)
VALUES (1, 'open', 1, 1, 2, 0, 0, NULL);

INSERT INTO `alembic_version` (`version_num`) VALUES ('0021');
