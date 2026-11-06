using Messenger.Application.Abstractions;
using Messenger.Domain.Conversations;
using Messenger.Domain.Messages;
using Messenger.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Messenger.Infrastructure.Persistence;

/// <summary>OPS-05: idempotent demo data — a dorm study crew with a few days of natural conversation.</summary>
public sealed class DemoSeeder(MessengerDbContext db, IPasswordHasher hasher, IConversationRepository conversations, IClock clock,
    ILogger<DemoSeeder> logger)
{
    private static readonly (string Key, string Email, string Display, bool Admin)[] People =
    [
        ("alice", "alice@migly.local", "Alice Kovalenko", false),
        ("bohdan", "bohdan@migly.local", "Bohdan Melnyk", false),
        ("kate", "kate@migly.local", "Kateryna Shevchuk", false),
        ("dmytro", "dmytro@migly.local", "Dmytro Bondar", false),
        ("iryna", "iryna@migly.local", "Iryna Tkachenko", false),
        ("admin", "admin@migly.local", "Migly Admin", true),
    ];

    // (conversation, sender, text, minutes since start, reply-to index within the same conversation script)
    private static readonly (string Conv, string From, string Text, int Minute, int? ReplyTo)[] Script =
    [
        ("crew", "alice", "Morning, room 412! Who's alive after yesterday's lab marathon?", 0, null),
        ("crew", "bohdan", "Barely. I dreamt about foreign keys.", 4, null),
        ("crew", "kate", "Same. Coffee in the kitchen in 10?", 6, null),
        ("crew", "dmytro", "I'm in, bringing the good beans from home", 7, null),
        ("crew", "iryna", "Save me a cup, I'm still in the shower 🚿", 9, null),
        ("crew", "alice", "Reminder: Lab 01 demo is on Friday. We need two users chatting live for the recording.", 45, null),
        ("crew", "bohdan", "I can be the second user. Should I write something smart?", 47, 5),
        ("crew", "kate", "Just don't write 'test 1 test 2' again 😅", 48, 6),
        ("crew", "bohdan", "No promises", 49, null),
        ("crew", "dmytro", "Does anyone know if the REST lab needs rate limiting for Standard or only Advanced?", 180, null),
        ("crew", "iryna", "Only Advanced. But it's like ten lines with the built-in middleware.", 184, 9),
        ("crew", "dmytro", "Nice, then I'm doing it tonight", 186, null),
        ("crew", "alice", "Pizza tonight? The place near the bus stop has the two-for-one deal on Tuesdays.", 600, null),
        ("crew", "kate", "Yes please. Margherita for me", 603, null),
        ("crew", "iryna", "Four cheese 🧀", 604, null),
        ("crew", "bohdan", "Pepperoni, extra spicy", 606, null),
        ("crew", "dmytro", "I'll pay this time, you covered me last week", 610, null),
        ("crew", "alice", "Ordered! Should arrive around 20:30", 625, null),
        ("crew", "kate", "Is the laundry room free? Someone left a red sock in machine 2 again", 1440, null),
        ("crew", "bohdan", "Not mine, I only own black socks", 1442, null),
        ("crew", "iryna", "Mine 🙈 sorry, grabbing it now", 1445, null),
        ("crew", "alice", "Who has the notes from the databases lecture? I fell asleep around normal forms.", 1520, null),
        ("crew", "dmytro", "Uploaded them to the shared drive, folder 'week 5'", 1524, 21),
        ("crew", "alice", "You're a legend", 1525, null),
        ("crew", "kate", "Quick poll: movie night on Saturday or board games?", 2900, null),
        ("crew", "bohdan", "Board games! I'll bring Catan", 2903, 24),
        ("crew", "iryna", "Movie, but only if it's not another horror", 2905, 24),
        ("crew", "dmytro", "Board games then, horror wins every movie vote anyway", 2908, null),
        ("crew", "alice", "Board games it is. Saturday 19:00 in the common room", 2912, null),
        ("crew", "kate", "Heading to the library, anyone need anything printed?", 3200, null),
        ("crew", "iryna", "Can you print my lab report? Sending the PDF in a sec", 3203, 29),

        ("alice-bohdan", "alice", "Hey, are you ready for class?", 30, null),
        ("alice-bohdan", "bohdan", "On my way, two minutes", 31, null),
        ("alice-bohdan", "alice", "Grab me a seat near the window if you're first", 32, null),
        ("alice-bohdan", "bohdan", "Done. Professor is late anyway", 36, null),
        ("alice-bohdan", "alice", "Can you review my pull request tonight? It's the message search part.", 1500, null),
        ("alice-bohdan", "bohdan", "Sure, after dinner. Does it handle deleted messages?", 1503, null),
        ("alice-bohdan", "alice", "Yes, deleted and pre-join messages are filtered out. There's a test for it.", 1505, 5),
        ("alice-bohdan", "bohdan", "Looks good, left two small comments", 1620, null),
        ("alice-bohdan", "alice", "Thanks! Fixed both", 1640, null),
        ("alice-bohdan", "bohdan", "See you at board games on Saturday", 2915, null),

        ("alice-kate", "kate", "Are you going home this weekend?", 900, null),
        ("alice-kate", "alice", "No, staying for the demo prep. You?", 905, null),
        ("alice-kate", "kate", "Staying too. Want to practise the defence questions together?", 907, null),
        ("alice-kate", "alice", "Yes! Especially 'what happens if two messages arrive in the same millisecond'", 910, null),
        ("alice-kate", "kate", "Easy: sequence numbers per conversation, time is not used for ordering", 912, 3),
        ("alice-kate", "alice", "Exactly. Sunday at 11?", 913, null),
        ("alice-kate", "kate", "Perfect, I'll bring cookies", 915, null),

        ("bohdan-dmytro", "dmytro", "Bro, do you still have my charger?", 400, null),
        ("bohdan-dmytro", "bohdan", "Yes, sorry! Leaving it on your desk", 410, null),
        ("bohdan-dmytro", "dmytro", "Thanks. Gym tomorrow at 7?", 412, null),
        ("bohdan-dmytro", "bohdan", "Make it 8, I'm not a morning person", 430, null),
        ("bohdan-dmytro", "dmytro", "Deal", 431, null),
    ];

    public async Task SeedAsync(string password, CancellationToken ct)
    {
        if (await db.UserSet.AnyAsync(u => u.UsernameNormalized == "alice", ct))
        {
            logger.LogInformation("Demo data already present; skipping seed");
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var start = clock.UtcNow.AddDays(-3).Date.AddHours(8);
            var origin = new DateTimeOffset(start, TimeSpan.Zero);
            var hash = hasher.Hash(password);

            var users = new Dictionary<string, User>();
            foreach (var (key, email, display, admin) in People)
            {
                var user = User.Register(email, key, display, hash, origin.AddDays(-30));
                user.ConfirmEmail();
                if (admin)
                {
                    user.PromoteToAdmin();
                }

                user.TouchLastSeen(origin.AddDays(3));
                db.UserSet.Add(user);
                users[key] = user;
            }

            await db.SaveChangesAsync(ct);

            var crew = Conversation.CreateGroup(users["alice"].Id, "Room 412 · Study crew",
                [users["bohdan"].Id, users["kate"].Id, users["dmytro"].Id, users["iryna"].Id], origin.AddDays(-1)).Value;
            crew.ChangeRole(users["alice"].Id, users["bohdan"].Id, ParticipantRole.Admin);
            db.ConversationSet.Add(crew);
            await db.SaveChangesAsync(ct);

            var conversationIds = new Dictionary<string, Guid> { ["crew"] = crew.Id };
            foreach (var pair in new[] { "alice-bohdan", "alice-kate", "bohdan-dmytro" })
            {
                var parts = pair.Split('-');
                conversationIds[pair] = await conversations.GetOrCreateDirectAsync(users[parts[0]].Id, users[parts[1]].Id, origin, ct);
            }

            var sent = new Dictionary<string, List<Message>>();
            foreach (var line in Script)
            {
                var at = origin.AddMinutes(line.Minute);
                var conversationId = conversationIds[line.Conv];
                var list = sent.TryGetValue(line.Conv, out var existing) ? existing : sent[line.Conv] = [];
                var sequence = await conversations.AllocateSequenceAsync(conversationId, at, true, ct);
                var sender = users[line.From];
                var message = Message.Create(conversationId, sequence!.Value, sender.Id, sender.Username, sender.DisplayName, line.Text,
                    Guid.NewGuid(), line.ReplyTo is { } index ? list[index].Id : null,
                    line.Text.Contains("PDF") ? [("lab-report-iryna.pdf", 482_113, "application/pdf")] : [], at);
                db.MessageSet.Add(message);
                list.Add(message);
            }

            await db.SaveChangesAsync(ct);

            // An edit (with history) and a tombstone, as in a real chat.
            var typo = sent["crew"][11];
            var editSequence = await conversations.AllocateSequenceAsync(crew.Id, origin.AddMinutes(188), false, ct);
            typo.Edit(typo.SenderId, "Nice, then I'm doing it tonight after the gym 💪", editSequence!.Value, origin.AddMinutes(188));
            var oops = sent["crew"][8];
            var deleteSequence = await conversations.AllocateSequenceAsync(crew.Id, origin.AddMinutes(50), false, ct);
            oops.Delete(oops.SenderId, deleteSequence!.Value, origin.AddMinutes(50));
            await db.SaveChangesAsync(ct);

            // Mixed read states: everything delivered; Iryna and Dmytro have unread messages in the crew chat.
            var lastSeen = origin.AddMinutes(3300);
            foreach (var (conv, conversationId) in conversationIds)
            {
                var last = sent[conv].Max(m => m.Sequence);
                var participantIds = await db.ParticipantSet.Where(p => p.ConversationId == conversationId).Select(p => p.UserId).ToListAsync(ct);
                foreach (var userId in participantIds)
                {
                    var readUpTo = conv == "crew" && (userId == users["iryna"].Id || userId == users["dmytro"].Id) ? last - 4 : last;
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO message_receipts (message_id, user_id, delivered_at, read_at)
                        SELECT m.id, {userId}, {lastSeen}, CASE WHEN m.sequence <= {readUpTo} THEN {lastSeen} END
                        FROM messages m
                        WHERE m.conversation_id = {conversationId} AND m.sender_id <> {userId} AND m.deleted_at IS NULL
                        ON CONFLICT (message_id, user_id) DO NOTHING
                        """, ct);
                    await db.ParticipantSet.Where(p => p.ConversationId == conversationId && p.UserId == userId)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastReadSequence, readUpTo), ct);
                }
            }

            await transaction.CommitAsync(ct);
        });

        logger.LogInformation("Seeded demo users and conversations");
    }
}
