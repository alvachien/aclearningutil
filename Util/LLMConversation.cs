namespace aclearningutil.Util
{
    public class LLMConversationMessage
    {
        public string role { get; set; }
        public string content { get; set; }
    }


    public class LLMConversation
    {
        public string model { get; set; }
        public LLMConversationMessage[] messages { get; set; }
    }

    public class LLMReplyContent
    {
        public string Content { get; set; }
    }

}
