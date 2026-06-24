namespace aclearningutil.Models
{
    public class LLMConversationMessage
    {
        public LLMConversationMessage()
        {
            role = string.Empty;
            content = string.Empty;
        }

        public string role { get; set; }
        public string content { get; set; }
    }
}
