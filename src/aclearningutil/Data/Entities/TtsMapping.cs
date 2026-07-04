namespace aclearningutil.Data.Entities;

public class TtsMapping
{
    public int Id { get; set; }
    public string Sentence { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
