namespace LogForesight.Web.Controllers.Api;

public class SetupGuideDto
{
    public bool Hidden { get; set; }
}

public class SetSetupGuideRequest
{
    public bool Hidden { get; set; }
}

public class NotePhrasesDto
{
    public List<string> Phrases { get; set; } = new();
    public bool IsDefault { get; set; }
}

public class SetNotePhrasesRequest
{
    public List<string?>? Phrases { get; set; }
}
