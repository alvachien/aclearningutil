using System.Text.Json.Serialization;

namespace aclearningutil.Data.Entities;

public class UserLearningRating
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int ContentId { get; set; }
    public int? ItemId { get; set; }
    public DateTime ScoreDate { get; set; }
    public byte Rating { get; set; }

    /// <summary>
    /// FK navigation, kept for EF's relationship configuration only. The ratings
    /// endpoints never send it: a client asking for the ratings of one content file
    /// already knows which content it is, and echoing the whole row once per rating
    /// just multiplies the payload (the UI's UserLearningRating type has no such field).
    /// </summary>
    [JsonIgnore]
    public LearningContent? Content { get; set; }
}
