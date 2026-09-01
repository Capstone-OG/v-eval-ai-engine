using V_Eval_Ai_Engine.Domain.ValueObjects;

namespace V_Eval_Ai_Engine.Domain.Entities;

/// <summary>
/// Đại diện cho phiên gia sư ảo Socratic hỗ trợ học sinh giải đáp thắc mắc câu hỏi
/// </summary>
public class TutorSession
{
    public string SessionId { get; private set; }
    public string StudentId { get; private set; }
    public string QuestionId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public bool IsActive { get; private set; }

    private readonly List<DialogueMessage> _messages = new();
    public IReadOnlyCollection<DialogueMessage> Messages => _messages.AsReadOnly();

    public TutorSession(string studentId, string questionId)
    {
        if (string.IsNullOrWhiteSpace(studentId))
            throw new ArgumentException("StudentId không được để trống.", nameof(studentId));
        if (string.IsNullOrWhiteSpace(questionId))
            throw new ArgumentException("QuestionId không được để trống.", nameof(questionId));

        SessionId = Guid.NewGuid().ToString();
        StudentId = studentId;
        QuestionId = questionId;
        CreatedAt = DateTime.UtcNow;
        IsActive = true;
    }

    public void AddMessage(string role, string content)
    {
        _messages.Add(new DialogueMessage
        {
            Role = role,
            Content = content,
            Timestamp = DateTime.UtcNow
        });
    }

    public void EndSession()
    {
        IsActive = false;
    }
}
