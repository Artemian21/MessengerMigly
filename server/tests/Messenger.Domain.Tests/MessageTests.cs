using Messenger.Domain.Common;
using Messenger.Domain.Messages;
using Shouldly;

namespace Messenger.Domain.Tests;

public sealed class MessageTests
{
    private static readonly DateTimeOffset Sent = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Author = Guid.NewGuid();

    private static Message NewMessage() =>
        Message.Create(Guid.NewGuid(), 5, Author, "alice", "Alice", "Hey, are you ready for class?", Guid.NewGuid(), null, [], Sent);

    [Fact]
    public void Author_can_edit_within_48_hours_and_history_keeps_previous_text()
    {
        var message = NewMessage();

        message.Edit(Author, "Hey, ready for the lab?", 9, Sent.AddHours(47)).IsSuccess.ShouldBeTrue();

        message.Body.ShouldBe("Hey, ready for the lab?");
        message.UpdatedSequence.ShouldBe(9);
        message.Sequence.ShouldBe(5);
        message.EditHistory.Single().PreviousBody.ShouldBe("Hey, are you ready for class?");
    }

    [Fact]
    public void Edit_after_48_hours_is_rejected()
    {
        var result = NewMessage().Edit(Author, "late", 9, Sent.AddHours(48).AddSeconds(1));

        result.Error!.Code.ShouldBe("edit_window_expired");
    }

    [Fact]
    public void Nobody_but_the_author_can_edit()
    {
        NewMessage().Edit(Guid.NewGuid(), "hijack", 9, Sent).Error!.Type.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public void Delete_is_a_tombstone_that_erases_body_and_history()
    {
        var message = NewMessage();
        message.Edit(Author, "edited", 6, Sent.AddMinutes(1));

        message.Delete(Author, 7, Sent.AddMinutes(2)).IsSuccess.ShouldBeTrue();

        message.Body.ShouldBeNull();
        message.IsDeleted.ShouldBeTrue();
        message.EditHistory.ShouldBeEmpty();
        message.Sequence.ShouldBe(5);
        message.UpdatedSequence.ShouldBe(7);
    }

    [Fact]
    public void Deleted_message_cannot_be_edited()
    {
        var message = NewMessage();
        message.Delete(Author, 6, Sent);

        message.Edit(Author, "again", 7, Sent).Error!.Code.ShouldBe("message_deleted");
    }

    [Fact]
    public void Only_author_deletes()
    {
        NewMessage().Delete(Guid.NewGuid(), 6, Sent).Error!.Type.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public void Creating_a_message_raises_message_sent()
    {
        NewMessage().DomainEvents.OfType<MessageSent>().ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(2, 0, 0, MessageStatus.Sent)]
    [InlineData(2, 1, 0, MessageStatus.Sent)]
    [InlineData(2, 2, 0, MessageStatus.Delivered)]
    [InlineData(2, 2, 1, MessageStatus.Delivered)]
    [InlineData(2, 2, 2, MessageStatus.Read)]
    [InlineData(0, 0, 0, MessageStatus.Sent)]
    public void Group_status_is_aggregated_over_all_recipients(int recipients, int delivered, int read, MessageStatus expected)
    {
        MessageStatusRules.Aggregate(recipients, delivered, read).ShouldBe(expected);
    }
}
