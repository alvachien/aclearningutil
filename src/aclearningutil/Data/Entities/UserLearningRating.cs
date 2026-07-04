namespace aclearningutil.Data.Entities;

public class UserLearningRating
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int ContentId { get; set; }
    public int? ItemId { get; set; }
    public DateTime ScoreDate { get; set; }
    public byte Rating { get; set; }

    public LearningContent? Content { get; set; }
}
