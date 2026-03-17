using System.Linq;
using Squid.Core.Services.Teams;

namespace Squid.UnitTests.Services.Teams;

public class TeamIdParserTests
{
    [Theory]
    [InlineData("1,2,3", new[] { 1, 2, 3 })]
    [InlineData("1,abc,3", new[] { 1, 3 })]
    [InlineData("0,1,0", new[] { 1 })]
    [InlineData("-1,2,-3", new[] { 2 })]
    [InlineData("abc,def", new int[] { })]
    [InlineData(" 1 , 2 , 3 ", new[] { 1, 2, 3 })]
    [InlineData(",,", new int[] { })]
    [InlineData("0,0,0", new int[] { })]
    public void ParseCsv_HandlesVariants(string input, int[] expected)
    {
        var result = TeamIdParser.ParseCsv(input);

        result.ShouldBe(expected.ToList());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseCsv_NullOrEmpty_ReturnsEmptyList(string input)
    {
        var result = TeamIdParser.ParseCsv(input);

        result.ShouldBeEmpty();
    }
}
