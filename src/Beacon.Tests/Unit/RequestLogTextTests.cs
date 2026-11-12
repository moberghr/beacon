using Beacon.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class RequestLogTextTests
{
    [TestCase("/beacon/api/projects", "/beacon/api/projects")]
    [TestCase("/beacon/api/a\r\nwarn: forged", "/beacon/api/a  warn: forged")]
    [TestCase("/beacon/api/a\nb", "/beacon/api/a b")]
    [TestCase("/beacon/api/a\u2028b\u2029c\u0085d", "/beacon/api/a b c d")]
    [TestCase("/beacon/api/a\tb\u0001c\u001bd", "/beacon/api/a b c d")]
    public void Path_IsWrittenOnOneLineWithoutControlCharacters(string path, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = new PathString(path);

        RequestLogText.Path(context.Request).Should().Be(expected);
    }

    [Test]
    public void Path_IsCutAtTheMaximumLength()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = new PathString("/" + new string('a', 2000));

        RequestLogText.Path(context.Request).Should().HaveLength(RequestLogText.MaxLength);
    }

    [Test]
    public void Method_IsWrittenOnOneLine()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST\r\nfail: forged";

        RequestLogText.Method(context.Request).Should().Be("POST  fail: forged");
    }

    [Test]
    public void AMissingPath_IsEmpty()
    {
        RequestLogText.OneLine(null).Should().BeEmpty();
    }
}
