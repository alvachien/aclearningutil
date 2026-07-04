namespace aclearningutil.Data.Entities;

public class UserLearningHistory
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int ContentId { get; set; }
    public int? ItemId { get; set; }
    public DateTime LearnDate { get; set; }
    public bool SuccessIndicator { get; set; }

    public LearningContent? Content { get; set; }
}
