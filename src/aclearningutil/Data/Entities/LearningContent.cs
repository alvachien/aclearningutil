namespace aclearningutil.Data.Entities;

public class LearningContent
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string NameChinese { get; set; } = string.Empty;
    public string NameEnglish { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public LearningContentCategory? Category { get; set; }
}
