using V_Eval_Ai_Engine.Domain.Entities;
using Xunit;

namespace V_Eval_Ai_Engine.Tests.Domain;

public class TutorSessionTests
{
    [Fact]
    public void Constructor_WithValidParameters_InitializesSessionCorrectly()
    {
        // Arrange
        var studentId = "student-123";
        var questionId = "question-456";

        // Act
        var session = new TutorSession(studentId, questionId);

        // Assert
        Assert.NotNull(session.SessionId);
        Assert.Equal(studentId, session.StudentId);
        Assert.Equal(questionId, session.QuestionId);
        Assert.True(session.IsActive);
        Assert.Empty(session.Messages);
        Assert.True(session.CreatedAt <= DateTime.UtcNow);
    }

    [Theory]
    [InlineData("", "question-123")]
    [InlineData("   ", "question-123")]
    [InlineData("student-123", "")]
    [InlineData("student-123", "   ")]
    public void Constructor_WithEmptyParameters_ThrowsArgumentException(string studentId, string questionId)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new TutorSession(studentId, questionId));
    }

    [Fact]
    public void AddMessage_AppendsMessage_And_EndSession_DeactivatesSession()
    {
        // Arrange
        var session = new TutorSession("student-1", "question-1");

        // Act
        session.AddMessage("User", "Làm sao giải bài này?");
        session.AddMessage("Assistant", "Hãy xem lại công thức định luật Ohm nhé.");

        // Assert
        Assert.Equal(2, session.Messages.Count);
        var firstMsg = session.Messages.First();
        Assert.Equal("User", firstMsg.Role);
        Assert.Equal("Làm sao giải bài này?", firstMsg.Content);

        // Act: End Session
        session.EndSession();

        // Assert
        Assert.False(session.IsActive);
    }
}
