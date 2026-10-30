namespace Portfolio.Analyst.Tests;

/// <summary>
/// The Fabric repo's shared redaction vector (<c>capstone/redaction-vector.json</c>, copied byte for byte
/// to <c>tests/vectors/redaction-vector.json</c>): every output was made by that repo's
/// <c>lakehouse.capstone.report.redact</c>, and <see cref="AnswerRedaction.Redact"/>, which
/// <see cref="RecordWriter"/> applies to every answer, must give the same text for every input.
/// </summary>
public sealed class RedactionVectorTests
{
    public static TheoryData<int> Cases()
    {
        var data = new TheoryData<int>();
        for (var index = 0; index < TestFiles.RedactionVector().Count; index++)
        {
            data.Add(index);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_case_redacts_as_the_fabric_repo_does(int index)
    {
        var item = TestFiles.RedactionVector()[index]!.AsObject();
        var input = item["input"]!.GetValue<string>();
        var output = item["output"]!.GetValue<string>();

        Assert.Equal(output, AnswerRedaction.Redact(input));
    }

    [Fact]
    public void The_vector_has_its_cases_and_is_ascii()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "redaction-vector.json"));
        Assert.All(bytes, b => Assert.True(b < 0x80));
        Assert.DoesNotContain((byte)'\r', bytes);
        var cases = TestFiles.RedactionVector();
        Assert.True(cases.Count >= 40);
        Assert.Contains(cases, c => c!["input"]!.GetValue<string>().Any(ch => ch > 0x7F));
    }
}
