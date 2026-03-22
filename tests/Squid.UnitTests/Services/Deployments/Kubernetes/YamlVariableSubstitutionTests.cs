using System.Collections.Generic;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.VariableSubstitution;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class YamlVariableSubstitutionTests
{
    private static VariableDictionary MakeVariables(Dictionary<string, string> vars)
    {
        var vd = new VariableDictionary();
        foreach (var kvp in vars) vd.Set(kvp.Key, kvp.Value);
        return vd;
    }

    [Fact]
    public void SubstituteInFiles_OctostacheVars_Replaced()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["deploy.yaml"] = Encoding.UTF8.GetBytes("replicas: #{Replicas}\nimage: #{Image}")
        };
        var vars = MakeVariables(new Dictionary<string, string>
        {
            ["Replicas"] = "3",
            ["Image"] = "nginx:latest"
        });

        var result = YamlVariableSubstitution.SubstituteInFiles(files, vars);

        var content = Encoding.UTF8.GetString(result["deploy.yaml"]);
        content.ShouldContain("replicas: 3");
        content.ShouldContain("image: nginx:latest");
    }

    [Fact]
    public void SubstituteInFiles_NoVars_Unchanged()
    {
        var original = "replicas: 1";
        var files = new Dictionary<string, byte[]>
        {
            ["deploy.yaml"] = Encoding.UTF8.GetBytes(original)
        };
        var vars = MakeVariables(new Dictionary<string, string>());

        var result = YamlVariableSubstitution.SubstituteInFiles(files, vars);

        Encoding.UTF8.GetString(result["deploy.yaml"]).ShouldBe(original);
    }

    [Fact]
    public void SubstituteInFiles_NonYamlFiles_Skipped()
    {
        var original = "replicas: #{Replicas}";
        var files = new Dictionary<string, byte[]>
        {
            ["script.sh"] = Encoding.UTF8.GetBytes(original)
        };
        var vars = MakeVariables(new Dictionary<string, string> { ["Replicas"] = "5" });

        var result = YamlVariableSubstitution.SubstituteInFiles(files, vars);

        Encoding.UTF8.GetString(result["script.sh"]).ShouldBe(original);
    }

    [Fact]
    public void SubstituteInFiles_MultipleYamlFiles_AllProcessed()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["deploy.yaml"] = Encoding.UTF8.GetBytes("ns: #{Namespace}"),
            ["service.yml"] = Encoding.UTF8.GetBytes("port: #{Port}")
        };
        var vars = MakeVariables(new Dictionary<string, string> { ["Namespace"] = "prod", ["Port"] = "8080" });

        var result = YamlVariableSubstitution.SubstituteInFiles(files, vars);

        Encoding.UTF8.GetString(result["deploy.yaml"]).ShouldContain("ns: prod");
        Encoding.UTF8.GetString(result["service.yml"]).ShouldContain("port: 8080");
    }

    [Fact]
    public void SubstituteInFiles_NullFiles_ReturnsNull()
    {
        var vars = MakeVariables(new Dictionary<string, string>());

        var result = YamlVariableSubstitution.SubstituteInFiles(null, vars);

        result.ShouldBeNull();
    }

    [Fact]
    public void SubstituteInFiles_NullVariables_ReturnsOriginal()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["deploy.yaml"] = Encoding.UTF8.GetBytes("replicas: #{Replicas}")
        };

        var result = YamlVariableSubstitution.SubstituteInFiles(files, null);

        result.ShouldBeSameAs(files);
    }

    [Fact]
    public void SubstituteInFiles_MixedYamlAndNonYaml_OnlyYamlSubstituted()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["deploy.yaml"] = Encoding.UTF8.GetBytes("replicas: #{Count}"),
            ["readme.txt"] = Encoding.UTF8.GetBytes("replicas: #{Count}")
        };
        var vars = MakeVariables(new Dictionary<string, string> { ["Count"] = "5" });

        var result = YamlVariableSubstitution.SubstituteInFiles(files, vars);

        Encoding.UTF8.GetString(result["deploy.yaml"]).ShouldContain("replicas: 5");
        Encoding.UTF8.GetString(result["readme.txt"]).ShouldContain("#{Count}");
    }
}
