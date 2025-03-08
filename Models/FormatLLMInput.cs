namespace aclearningutil.Models
{
    public class FormatLLMInput
    {
        public FormatLLMInput()
        {
            FormatType = "math";
            Context = string.Empty;
        }

        public string FormatType { get; set; }
        public string Context { get; set; }
    }
}
