namespace aclearningutil.Models
{
    public class LLMConversation
    {
        public LLMConversation() {
            model = string.Empty;
            messages = [];
        }

        public string model { get; set; }
        public LLMConversationMessage[] messages { get; set; }
    }
}
