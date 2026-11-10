# 8. Client (`client/`)

React 19 + Vite + TypeScript single-page app. In development Vite serves it on `:5173` and proxies `/api`, `/hubs`, `/docs`, `/health` to the API on `:5000` (same-origin behaviour). In production `npm run build` output (`client/dist`) is copied into the API image's `wwwroot` and served by ASP.NET Core.

Stack: React Router, TanStack Query (server state), Zustand (small client state), `@microsoft/signalr`, `react-virtuoso` (virtualised lists), Vitest + Testing Library.

## Folder layout (`client/src`)

| Path | Contents |
|---|---|
| `main.tsx`, `App.tsx` | Entry, providers, routes, session bootstrap, route guards |
| `api/` | `http.ts` (fetch wrapper), `endpoints.ts` (typed calls), `types.ts` (DTO mirrors) |
| `state/` | Zustand stores: `auth.ts`, `connectivity.ts`, `live.ts`, `summaries.ts` |
| `realtime/hub.ts` | SignalR client, cache updates, resync |
| `hooks/` | `useMessages.ts` (paged message query with older-page loading), `useSummary.ts` (start / poll / dismiss a summary) |
| `lib/` | Pure helpers: `messages.ts` (cache merge rules), `format.ts`, `errors.ts`, `conversations.ts` |
| `i18n/` | Translation module and `locales/uk.json`, `locales/en.json` |
| `components/` | `ui.tsx` (Avatar, Modal, Field, ErrorNotice, LanguageSwitch), `MessageBubble`, `Composer`, `UserPicker`, `GroupPanel`, `SummaryBanner` |
| `pages/` | `AuthPages`, `ChatsLayout`, `ChatPage`, `DraftChatPage`, `NewChatPage`, `SearchPage`, `SettingsPage`, `AdminPage` |
| `styles/` | `theme.css` (tokens), `app.css` (components) |
| `test/` | `setup.ts`, `factories.ts` |
| `public/` | `manifest.webmanifest`, `sw.js`, icons |

## Routes

| Path | Page | Guard |
|---|---|---|
| `/login`, `/register`, `/verify-email`, `/forgot-password`, `/reset-password`, `/auth/callback` | `AuthPages` | public |
| `/onboarding` | choose username (OAuth users) | signed in |
| `/` | chat list + welcome | signed in |
| `/chats/:conversationId` | chat window | signed in |
| `/direct/:peerId` | draft personal chat (created on first message) | signed in |
| `/new`, `/search`, `/settings` | new chat/group, search, settings | signed in |
| `/admin` | administration | system admin |

`RequireAuth` redirects to `/login` (remembering the target) and to `/onboarding` while `needsProfile` is true.

## Authentication on the client
- The **access token lives only in memory** (`state/auth.ts`). On load `SessionBootstrap` calls `POST /auth/refresh` (the HttpOnly cookie travels automatically) and shows "connecting…" until it answers.
- `api/http.ts`: attaches `Authorization`, refreshes proactively when the token expires within 60 s, and on `401` does **one shared** refresh (`refreshSession` returns the same promise to parallel callers) then retries. Every request carries `Accept-Language`; refresh/logout add `X-Requested-With: XMLHttpRequest`.
- `ApiError` exposes `status`, the problem document, and `field(name)` for 422 field errors.
- Cold start handling (OPS-07): requests slower than 3 s set `connectivity.waking`; GETs that fail with 502/503 or network errors are retried with backoff. Banners show "server is waking up".

## State

| Store | Contents |
|---|---|
| `useAuth` | `accessToken`, `expiresAt`, `user`, `status` (`loading|authenticated|anonymous`) |
| `useConnectivity` | `waking`, hub state (`connecting|connected|reconnecting|disconnected`) |
| `useLive` | presence by user id; typing indicators with 5 s expiry |
| TanStack Query | `['conversations']`, `['messages', id]` (`{items, hasOlder}`), members, search, sessions, admin lists |

## Messages and optimistic UI (`lib/messages.ts`)
- `mergeMessages(items, incoming)` is the single merge rule: confirmed messages replace their optimistic copy (matched by `clientMessageId`), a newer `updatedSequence` replaces an older revision, statuses never downgrade, order = server `sequence` with pending messages last.
- Sending: `optimisticMessage` is inserted with `pending: 'sending'` → `POST` → replaced by the server message. On failure it becomes `failed` with a **retry** button that re-sends with the *same* `clientMessageId` (the server returns the stored message if the first attempt actually landed).
- `applyStatus`, `applyDeletion` (tombstone + reply previews), `markFailed`, `canEdit` (author, ≤ 48 h).
- Message bodies are rendered as **plain text**; `dangerouslySetInnerHTML` is banned by ESLint.

## Chat window (`ChatPage`)
- `react-virtuoso` list with upward infinite scroll (`startReached` loads older pages via `before=<sequence>`), day dividers, author labels in groups, scroll-to-reply highlight.
- Opens the conversation on the hub, acknowledges delivered messages on first load, and marks read when the tab is visible.
- Composer: Enter sends, Shift+Enter new line, counter near the 4000-character limit (grapheme-aware), reply/edit context, throttled typing signal.
- Modals: message info (per-recipient delivery/read) and edit history (author only).
- Personal chat deletion = "clear for me"; group settings in `GroupPanel` (rename, members, roles, leave, delete).
- **"What did I miss" (`SummaryBanner`)**: when the chat is opened with at least `minMessages` unread (from `GET ai/config`, which also says whether the feature is on), a banner offers to summarize. Opening a chat marks it read, so `ChatPage` remembers the unread count and read position from that moment and the banner sends the position as `afterSequence`. States: offer → pending → ready / failed / empty. The result arrives as the hub event `SummaryCompleted`; `useSummary` polls `GET …/summary/{jobId}` every 3 s as a safety net. The text is rendered as plain text, carries an "AI-generated" note and says that it is sent to an AI service. A finished result is dropped when the chat is left; a running job is kept.

## Realtime (`realtime/hub.ts`)
Singleton `RealtimeClient`: starts after sign-in, automatic reconnect with backoff, updates the query cache from events (`MessageReceived/Updated/Deleted`, `MessageStatusChanged`, `ConversationUpdated/Read`, `Typing`, `PresenceChanged`) and the summary store (`SummaryCompleted`) and resyncs over REST after reconnect. Event handlers must not return values (SignalR would treat them as client results). Details in [Realtime](06-realtime.md).

## Internationalization (`i18n/`)
- Languages: **Ukrainian (default) and English**; resource files `locales/uk.json` and `locales/en.json`.
- API: `useT()` hook returns `t(key, params?)`; `t` (non-hook) for code outside React. Keys are typed (`TKey`) from `uk.json`.
- Interpolation `{name}`; plural forms by `Intl.PluralRules` when `params.count` is a number: Ukrainian `_one/_few/_many/_other`, English `_one/_other`.
- First visit follows `navigator.language` (`en*` → English else Ukrainian); the choice is stored in `localStorage` (`migly.lang`) and sets `<html lang>`. `App` re-mounts the route tree on language change.
- Dates/times use `localeTag(locale)` (`uk-UA` / `en-GB`) and are displayed in the viewer's time zone (timestamps arrive in UTC).
- The `Accept-Language` header makes server errors and emails follow the UI language.
- A test enforces key parity between files; plural suffixes may differ per language.

Adding text: add the key to **both** JSON files, use `t('key')`. Never inline user-visible strings.

## Theme (`styles/`)
**Neumorphism ("soft UI")**: one background colour, depth from paired light/dark shadows — raised for cards and buttons (`--raised`, `--raised-small`), pressed for inputs and active controls (`--pressed`). All colours, radii and shadows are tokens in `theme.css`, with a dark variant under `prefers-color-scheme: dark`. Swapping the visual identity means editing `theme.css` only. Layout is responsive: below 760 px the sidebar and chat are separate views.

## PWA
`manifest.webmanifest` + `sw.js` (registered only in production builds): offline app shell (network-first for navigations, cache-first for hashed assets), never caches `/api` or `/hubs`; Web Push display and click handling. Push is offered in Settings only when the server reports VAPID keys.

## Scripts
```bash
npm run dev        # Vite on :5173
npm run build      # tsc -b && vite build
npm run lint       # eslint, zero warnings allowed
npm test -- --run  # Vitest
```

## Adding a screen
1. Page component in `pages/`, route in `App.tsx` (wrap in `RequireAuth` if private).
2. Typed API call in `api/endpoints.ts` and DTO in `api/types.ts`.
3. Text in both locale files; use `useQuery`/`useMutation`.
4. Styles via existing tokens/classes in `app.css`.
