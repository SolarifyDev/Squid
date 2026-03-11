namespace Squid.Message.Models.Deployments.Interruption;

public class InterruptionForm
{
    public List<InterruptionFormElement> Elements { get; set; } = new();
    public Dictionary<string, string> Values { get; set; } = new();
}

public class InterruptionFormElement
{
    public string Name { get; set; }
    public InterruptionFormControl Control { get; set; }
}

public abstract class InterruptionFormControl
{
    public string Type { get; set; }
}

public class ParagraphControl : InterruptionFormControl
{
    public string Text { get; set; }

    public ParagraphControl() { Type = "Paragraph"; }
}

public class TextAreaControl : InterruptionFormControl
{
    public string Label { get; set; }

    public TextAreaControl() { Type = "TextArea"; }
}

public class SubmitButtonGroupControl : InterruptionFormControl
{
    public List<string> Buttons { get; set; } = new();

    public SubmitButtonGroupControl() { Type = "SubmitButtonGroup"; }
}
