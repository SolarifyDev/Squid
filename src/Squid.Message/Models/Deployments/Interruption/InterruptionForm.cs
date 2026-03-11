using System.Text.Json.Serialization;

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

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(ParagraphControl), "Paragraph")]
[JsonDerivedType(typeof(TextAreaControl), "TextArea")]
[JsonDerivedType(typeof(SubmitButtonGroupControl), "SubmitButtonGroup")]
public abstract class InterruptionFormControl;

public class ParagraphControl : InterruptionFormControl
{
    public string Text { get; set; }
}

public class TextAreaControl : InterruptionFormControl
{
    public string Label { get; set; }
}

public class SubmitButtonGroupControl : InterruptionFormControl
{
    public List<string> Buttons { get; set; } = new();
}
