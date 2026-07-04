namespace aclearningutil.Models
{
    public class SentenceJsonMap
    {
        public SentenceJsonMap() 
        {
            Sentence = string.Empty;
            FileName = string.Empty;
        }

        public string Sentence { get; set; }
        public string FileName { get; set; }
    }
}
