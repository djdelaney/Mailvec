using System.Text;
using Mailvec.Core.Parsing;
using Mailvec.Parsing.Contracts;

namespace Mailvec.Core.Tests.Parsing;

/// <summary>
/// The pre-deserialization shape guard. The point is not only that oversized
/// shapes are refused but that refusing them costs nothing: the review
/// measured a 300 KB response of 100,000 empty objects allocating ~15 MB once
/// deserialized, and a check that ran after that would be too late.
/// </summary>
public class ResponseShapeTests
{
    private static byte[] Attachments(int n) =>
        Encoding.UTF8.GetBytes("{\"attachments\":[" + string.Join(",", Enumerable.Repeat("{}", n)) + "]}");

    [Fact]
    public void An_oversized_array_is_refused_without_materialising_it()
    {
        var json = Attachments(100_000); // ~300 KB, the review's fixture
        var before = GC.GetAllocatedBytesForCurrentThread();

        var ex = Should.Throw<ParseException>(() => ResponseShape.Check(json));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        ex.Kind.ShouldBe(ParseFailureKind.Crashed);
        ex.Message.ShouldContain("array longer than");
        allocated.ShouldBeLessThan(64 * 1024, "the scan must not build the graph it is refusing (deserializing this allocates ~15 MB)");
    }

    [Fact]
    public void An_honest_response_passes()
    {
        ResponseShape.Check(Attachments(ResponseShape.MaxArrayElements)); // at the limit, not over
        ResponseShape.Check("{\"pageCount\":2,\"pages\":[\"AAAA\",\"BBBB\"]}"u8);
        ResponseShape.Check("{\"text\":null}"u8);
        ResponseShape.Check("[1,[2,[3,[4]]]]"u8); // nesting is fine; each level is small
    }

    [Fact]
    public void Nested_arrays_are_counted_per_level_not_in_total()
    {
        // 5,000 elements at each of two levels is 10,000 values but no array
        // over the limit; the limit is per array because that is where the
        // deserializer's per-element allocation lives.
        var inner = "[" + string.Join(",", Enumerable.Repeat("1", 5_000)) + "]";
        var json = "[" + string.Join(",", Enumerable.Repeat(inner, 2)) + "]";
        ResponseShape.Check(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public void Too_many_tokens_in_total_is_refused()
    {
        // Under the per-array limit everywhere (objects, not arrays), over the
        // whole-document budget: 400k objects of one property each.
        var sb = new StringBuilder("{");
        for (var i = 0; i < 400_000; i++) sb.Append(i == 0 ? "" : ",").Append('"').Append(i).Append("\":{\"a\":1}");
        sb.Append('}');

        Should.Throw<ParseException>(() => ResponseShape.Check(Encoding.UTF8.GetBytes(sb.ToString())))
            .Message.ShouldContain("tokens");
    }

    [Fact]
    public void Malformed_json_is_Crashed_here_too()
    {
        Should.Throw<ParseException>(() => ResponseShape.Check("{\"attachments\":["u8)).Kind.ShouldBe(ParseFailureKind.Crashed);
    }
}
