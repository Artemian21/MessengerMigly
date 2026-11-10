# Traceability: requirement → code → test

Paths are relative to `server/src` (`S/`), `server/tests` (`T/`) and `client/src` (`C/`). Integration tests run against real PostgreSQL (Testcontainers).

## Auth (AUTH)

| Req | Implementation | Tests |
|---|---|---|
| AUTH-01 | `S/Messenger.Application/Auth/AuthCommands.cs` `RegisterHandler` | `AuthFlowTests.Unverified_account_cannot_sign_in_and_duplicate_registration_is_neutral`, `AuthHandlerTests.Register_with_known_email_is_neutral`, `…Register_with_taken_username_conflicts` |
| AUTH-02 | `S/Messenger.Infrastructure/Security/SecurityServices.cs` `BCryptPasswordHasher` (EnhancedHashPassword, WF 11); `InputRules.ValidatePassword` | `UserAndRulesTests.Password_policy_enforces_length_and_bcrypt_byte_limit`, all login tests |
| AUTH-03 | `VerifyEmailHandler`, `UserTokenIssuer`, `UserToken.Use` | `AuthFlowTests.Verification_token_is_single_use`, `UserAndRulesTests.User_token_is_single_use_and_expires` |
| AUTH-04 | `LoginHandler`, `SessionIssuer`, `JwtTokenService`, `AuthController.Login` | `AuthFlowTests.Register_verify_login_refresh_rotation_reuse_detection_and_logout` |
| AUTH-05 | `RefreshHandler`, `RefreshToken.IsWithinRotationGrace`, `RefreshCookie`, `RequireSameOriginAttribute` | `AuthFlowTests.Reuse_outside_grace_revokes_the_whole_session`, `…Refresh_without_csrf_header_is_rejected`, `AuthHandlerTests.Reusing_a_rotated_token_outside_grace_revokes_the_family`, `…Parallel_tab_within_grace_gets_access_token_without_new_cookie`, `…Active_refresh_token_is_rotated` |
| AUTH-06 | `LogoutHandler` | `AuthFlowTests.Register_verify_login_refresh_rotation_reuse_detection_and_logout` |
| AUTH-07 | `ForgotPasswordHandler`, `ResetPasswordHandler` | `AuthFlowTests.Password_reset_revokes_every_session` |
| AUTH-08 | `ChangePasswordHandler` | `AuthFlowTests.Change_password_keeps_current_session_and_revokes_others` |
| AUTH-09 (S) | `ExternalAuthController`, `ExternalLoginHandler`, `LinkExternalLoginHandler`, `CompleteProfileHandler` | Not covered by automated tests (needs provider credentials); policy is unit-level code review only. |
| AUTH-10 | `User.RecordFailedLogin`, `RateLimitPolicies.Auth` | `AuthFlowTests.Lockout_after_repeated_failures`, `RateLimitTests.Auth_endpoints_return_429_with_retry_after`, `UserAndRulesTests.Lockout_after_repeated_failures` |
| AUTH-11 (C) | `ListSessionsHandler`, `RevokeSessionHandler` | `AuthFlowTests.Sessions_can_be_listed_and_revoked` |
| AUTH-12 | `AuthErrors.InvalidCredentials`, JWT bearer, `AuthorizationBehavior` | `AuthFlowTests.Wrong_password_is_401_with_generic_message`, `…Requests_without_token_are_401`, `…Tampered_token_is_401`, `AuthHandlerTests.Unknown_email_gets_the_same_error_as_wrong_password` |
| AUTH-13 | `JwtTokenService.CreateAccessToken` (sub, sid, role, jti, exp) | covered by every authenticated integration test |

## Users (USR)

| Req | Implementation | Tests |
|---|---|---|
| USR-01 | `User.Register` (GUID v7) | domain + integration tests |
| USR-02 | `InputRules`, partial unique index `ix_users_username_normalized`, `ChangeUsernameHandler` | `UserAndRulesTests.Username_format`, `AccountAdminAndOpsTests.Username_change_conflicts_case_insensitively` |
| USR-03 | `SearchUsersHandler` | `GroupsSearchAndBolaTests.User_search_finds_by_prefix_and_paginates` |
| USR-04 | `GetMeHandler`, `UpdateProfileHandler` | `AuthFlowTests…` (`/users/me`), client `SettingsPage` |
| USR-05 | `DeleteAccountHandler`, `User.Delete`, `Conversation.Leave` | `AccountAdminAndOpsTests.Deleting_account_frees_username_transfers_ownership_and_keeps_messages`, `UserAndRulesTests.Deleting_user_anonymizes_and_frees_username` |
| USR-06 (S) | `GetUserHandler`, `PresenceRules` | `RealtimeTests.Presence_goes_offline_only_after_the_grace_period_and_only_for_contacts` |

## Conversations (CNV)

| Req | Implementation | Tests |
|---|---|---|
| CNV-01 | `ConversationRepository.GetOrCreateDirectAsync` (ON CONFLICT on `DirectKey`), self-chat check | `MessagingTests.Concurrent_first_messages_create_exactly_one_direct_chat`, `…Self_chat_is_rejected`, `ConversationTests.Direct_key_is_order_independent` |
| CNV-02 | `SendDirectMessageHandler`, `GetDirectByPeerHandler`, `C/pages/DraftChatPage.tsx` | `MessagingTests.Direct_chat_is_created_by_first_message_and_history_survives_in_order` |
| CNV-03 | `Conversation.Clear`, `ConversationReader` hide rule | `MessagingTests.Clearing_a_personal_chat_hides_it_until_a_new_message_arrives` |
| CNV-04 | `Conversation.CreateGroup` | `ConversationTests.Group_requires_at_least_three_members_including_creator`, `GroupsSearchAndBolaTests.Group_needs_three_members_and_owner_manages_it` |
| CNV-05 | `ConversationParticipant.Join` (`VisibleFromSequence`) | `ConversationTests.New_member_sees_only_messages_after_joining`, `GroupsSearchAndBolaTests.New_member_sees_only_messages_after_joining` |
| CNV-06 | `Conversation.Leave` | `ConversationTests.Owner_leaving_transfers_ownership_to_oldest_admin`, `GroupsSearchAndBolaTests.Owner_leaving_transfers_ownership_and_deleted_group_rejects_messages` |
| CNV-07 | `Conversation.Delete` | same as above, `ConversationTests.Deleted_group_rejects_changes` |
| CNV-08 (S) | `AddMembers`, `RemoveMember`, `ChangeRole` | `ConversationTests.Admin_can_remove_member_but_not_owner_or_other_admin`, `…Only_owner_changes_roles_and_ownership_cannot_be_assigned` |
| CNV-09 | `ConversationReader` (one grouped query: unread, preview, peer) | `MessagingTests.Read_marks_update_unread_counts_and_author_status` |
| CNV-10 (C) | `Conversation.Rename` | `GroupsSearchAndBolaTests.Group_needs_three_members_and_owner_manages_it` |

## Messages (MSG)

| Req | Implementation | Tests |
|---|---|---|
| MSG-01 | `MessageSender.SendAsync`, `ConversationRepository.AllocateSequenceAsync` | `MessagingTests.Concurrent_sends_get_unique_monotonic_sequences` |
| MSG-02 | `InputRules.ValidateMessageBody`, `SendMessageValidator`, 422 mapping | `MessagingTests.Invalid_messages_are_422_with_body_error`, `…Message_over_4000_characters_is_422`, `BehaviorTests.Validation_failure_returns_validation_error_with_camel_case_fields`, `UserAndRulesTests.Invalid_message_bodies_are_rejected` |
| MSG-03 | `GetMessagesHandler` (before/after/limit ≤ 50), `C/hooks/useMessages.ts` | `MessagingTests.Cursor_pagination_loads_older_pages` |
| MSG-04 | `Message.Edit`, `GetMessageHistoryHandler` | `MessageTests.Author_can_edit_within_48_hours_and_history_keeps_previous_text`, `…Edit_after_48_hours_is_rejected`, `MessagingTests.Edit_history_and_info_are_author_only_and_edits_appear_in_resync` |
| MSG-05 | `Message.Delete` (tombstone) | `MessageTests.Delete_is_a_tombstone_that_erases_body_and_history`, `MessagingTests.Delete_is_a_tombstone_and_replies_survive` |
| MSG-06 | reply validation in `MessageSender`, `MessageReader` reply preview | `MessagingTests.Delete_is_a_tombstone_and_replies_survive` |
| MSG-07 | `SearchMessagesHandler` + trigram index migration | `GroupsSearchAndBolaTests.Search_respects_join_clear_and_delete_visibility`, `…Search_treats_wildcards_literally` |
| MSG-08 | `Message.Edit` author check, `GroupPermissions` (no edit permission) | `MessageTests.Nobody_but_the_author_can_edit` |
| MSG-09 (C) | `MessageAttachment`, `AttachmentsValidator` | `MessagingTests.Attachment_metadata_is_stored_with_the_message` |
| MSG-10 | `Message.Create` snapshots, `MessageReader.Sender` | `AccountAdminAndOpsTests.Deleting_account_frees_username_transfers_ownership_and_keeps_messages` |

## Statuses and sync (STS)

| Req | Implementation | Tests |
|---|---|---|
| STS-01/05 | `MessageStatusRules.Aggregate`, `MessageReader.GetStatusesAsync` | `MessageTests.Group_status_is_aggregated_over_all_recipients` |
| STS-02 | `MessageRepository.MarkDeliveredAsync` (ON CONFLICT DO NOTHING), hub `AckDelivered` | `RealtimeTests.Send_receive_ack_read_produces_status_changes_for_the_author` (second ack returns 0) |
| STS-03 | `MarkReadHandler`, `MessageRepository.MarkReadAsync` | `RealtimeTests.Send_receive_ack_read…`, `MessagingTests.Read_marks_update_unread_counts_and_author_status` |
| STS-04 | `message_receipts` table, receipts only for other users' messages | `MessagingTests.Read_marks…` |
| STS-06 (S) | `GetMessageInfoHandler` | `MessagingTests.Read_marks…`, `…Edit_history_and_info_are_author_only…` |
| STS-07 | `ConversationReader` unread count | `MessagingTests.Direct_chat_is_created…`, `…Read_marks…` |
| STS-08 | `GetMessagesHandler` `after` on `UpdatedSequence`, `GetStatusesHandler`, `C/realtime/hub.ts resync` | `RealtimeTests.Missed_messages_are_recovered_through_rest_resync_after_reconnect` |

## Presence and push (PRS / NTF)

| Req | Implementation | Tests |
|---|---|---|
| PRS-01/02 | `Api/Realtime/PresenceTracker.cs` | `RealtimeTests.Presence_goes_offline_only_after_the_grace_period_and_only_for_contacts` |
| PRS-03 | hub `Typing` (2 s throttle per connection), `C/state/live.ts` (5 s expiry) | `RealtimeTests.Typing_reaches_others_who_opened_the_conversation_and_outsiders_cannot_open_it` |
| PRS-04 (S) | `PresenceRules.FilterSharedAsync`, `GetPresenceHandler` | `RealtimeTests.Presence_goes_offline…` (stranger gets empty snapshot) |
| NTF-01 (S) | `PushOnMessageSent`, `WebPushSender`, `C/../public/sw.js`, `SettingsPage.PushCard` | Not covered automatically (needs VAPID keys + browser); disabled when keys are absent. |

## RBAC / audit

| Req | Implementation | Tests |
|---|---|---|
| RBAC-01 | `IAdminRequest` in `AuthorizationBehavior`, `AdminFeatures.cs` | `AccountAdminAndOpsTests.Admin_endpoints_require_admin_role_and_never_expose_messages`, `BehaviorTests.Non_admin_cannot_use_admin_requests` |
| RBAC-02 | `GroupPermissions` | `ConversationTests.*` |
| AUD-01 (C) | `AuditLogger`, `ListAuditLogHandler` | `AccountAdminAndOpsTests.Admin_endpoints…` (audit entry asserted) |

## API / NFR / OPS

| Req | Implementation | Tests |
|---|---|---|
| API-01/08 | routes under `/api/v1`, camelCase JSON, UTC `DateTimeOffset` | `MessagingTests.Direct_chat_is_created…` (UTC offset asserted) |
| API-02 | Conversations/Messages controllers (≥ 5 CRUD endpoints) | integration suite |
| API-03 | `Problems`, `GlobalExceptionHandler`, `InvalidModelStateResponseFactory` (422) | `AuthFlowTests.Validation_errors_are_422_problem_details_with_field_errors`, `…Malformed_json_is_422_not_500`, `AccountAdminAndOpsTests.Unknown_api_route_is_json_404_and_security_headers_are_set` |
| API-04 | `OpenApiSetup`, Scalar at `/docs` | `AccountAdminAndOpsTests.Health_docs_and_openapi_are_public` |
| API-05 | `RateLimitingSetup` | `RateLimitTests.Auth_endpoints_return_429_with_retry_after` |
| API-06 | `PagedResult` (`items,total,next`), cursor for messages | `AccountAdminAndOpsTests.Admin_endpoints…` (`next` asserted), `MessagingTests.Cursor_pagination…` |
| API-07 | soft delete: users (`Status=Deleted`), messages (tombstone), groups (`DeletedAt`) | see USR-05, MSG-05, CNV-07 |
| API-09 | CORS allow-list only from `Cors:AllowedOrigins` (empty = same-origin) | config |
| API-10 | `HealthController` | `AccountAdminAndOpsTests.Health_docs_and_openapi_are_public` |
| API-11 | `LoggingSetup`, `UseSerilogRequestLogging` (path only, userId, traceId) | manual log inspection (no tokens / bodies / hub query in logs) |
| API-12 | `.env.example`, `.gitignore`, gitleaks job, `ValidateSecrets` | CI `secrets` job |
| API-13 | unique `(sender_id, client_message_id)`, `IRetryOnUniqueViolation` | `MessagingTests.Send_is_idempotent_by_client_message_id` |
| API-14 | EF LINQ + interpolated `SqlQuery`/`ExecuteSqlInterpolated` (parameters), escaped LIKE | `BehaviorTests.Like_patterns_escape_wildcards`, `GroupsSearchAndBolaTests.Search_treats_wildcards_literally` |
| NFR-01 | `AuthorizationBehavior` (`IConversationMemberRequest`, `IMessageAccessRequest`) | `GroupsSearchAndBolaTests.Bola_outsider_cannot_touch_someone_elses_conversation` |
| NFR-02 | access token in memory (`C/state/auth.ts`), cookie flags, CSRF filter | `AuthFlowTests.Register_verify_login…` (HttpOnly + SameSite asserted), `C/api/http.test.ts` |
| NFR-03 | `SecurityHeadersMiddleware`; React text rendering; ESLint ban on `dangerouslySetInnerHTML` | `C/components/MessageBubble.test.tsx`, `AccountAdminAndOpsTests.Unknown_api_route…` |
| NFR-04 | send in one transaction; events after commit (`UnitOfWork`, `DomainEventDispatcher`) | `RealtimeTests.*` |
| NFR-08 | layer rules | `LayerTests.*` |
| NFR-12 | `IClock`, `DateTimeOffset` UTC, `C/lib/format.ts` local display | `C/lib/format.test.ts` |
| OPS-02 | `.github/workflows/ci.yml` | — |
| OPS-03 | test projects | 129 server + 26 client tests + 1 E2E |
| API-03 (no 5xx on garbage) | validators reject control characters; `GlobalExceptionHandler` maps SQLSTATE 22xxx to 422 | `InputHardeningTests.*`, `UserAndRulesTests.Email_must_be_well_formed_and_free_of_control_characters` |
| OPS-04 | `DatabaseMigrator` (`pg_advisory_lock`) | every integration test run migrates a fresh DB |
| OPS-05 | `DemoSeeder` (`dotnet run -- seed`) | run locally |
| OPS-06 | `README.md`, `docs/requests.http` | — |
| OPS-07 | `C/state/connectivity.ts`, banners in `ChatsLayout`/`AuthPages` | manual |
| E2E | `e2e/tests/first-message.spec.ts` (runs against the Ukrainian UI) | Playwright |
| i18n (client, uk/en) | `C/i18n/index.ts`, `C/i18n/locales/{uk,en}.json`, `LanguageSwitch` in `C/components/ui.tsx`, `Accept-Language` in `C/api/http.ts` | `C/i18n/i18n.test.ts` (key parity, no empty strings, plural forms), `C/api/http.test.ts` |
| i18n (server, en/uk) | `S/Messenger.Api/Infrastructure/ApiLocalizer.cs`, `S/Messenger.Api/Resources/ApiMessages.*.resx`, `S/Messenger.Application/Resources/EmailTexts.*.resx`, `UseRequestLocalization` | `LocalizationTests.*` (422 in uk, 401 in uk/en, unsupported language → English, entity-aware 404, email language) |
| Theme (Make it yours) | `C/styles/theme.css` (Neumorphism tokens, light + dark), `C/styles/app.css` | visual check |
