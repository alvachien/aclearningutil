namespace aclearningutil.Models
{
    public class LLMConversation
    {
        public string model { get; set; }
        public LLMConversationMessage[] messages { get; set; }
    }
}
