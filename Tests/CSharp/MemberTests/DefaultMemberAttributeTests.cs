using System.Threading.Tasks;
using ICSharpCode.CodeConverter.Tests.TestRunners;
using Xunit;

namespace ICSharpCode.CodeConverter.Tests.CSharp.MemberTests;

public class DefaultMemberAttributeTests : ConverterTestBase
{
    [Fact]
    public async Task TestDefaultMemberAttributeConversionAsync()
    {
        await TestConversionVisualBasicToCSharpAsync(
            @"
<System.Reflection.DefaultMember(""Caption"")>
Public Class ClassWithReflectionDefaultMember
    Public Property Caption As String
End Class

<System.Reflection.DefaultMember(NameOf(LoosingProperties.Caption))>
Public Class LoosingProperties
    Public Property Caption As String

    Sub S()
        Dim x = New LoosingProperties()
        x.Caption = ""Hello""

        Dim y = New ClassWithReflectionDefaultMember() 'from C#
        y.Caption = ""World""
    End Sub
End Class", @"using System.Reflection;

[DefaultMember(""Caption"")]
public partial class ClassWithReflectionDefaultMember
{
    public string Caption { get; set; }
}

[DefaultMember(nameof(Caption))]
public partial class LoosingProperties
{
    public string Caption { get; set; }

    public void S()
    {
        var x = new LoosingProperties();
        x.Caption = ""Hello"";

        var y = new ClassWithReflectionDefaultMember(); // from C#
        y.Caption = ""World"";
    }
}
");
    }

    /// <summary>
    /// Regression test for https://github.com/icsharpcode/CodeConverter/issues/1091
    /// When an interface has [DefaultMember(""Value"")] and VB code explicitly accesses .Value,
    /// the converter must NOT strip the .Value member access in the C# output.
    /// </summary>
    [Fact]
    public async Task Issue1091_ExplicitAccessToDefaultMemberPropertyIsPreservedAsync()
    {
        await TestConversionVisualBasicToCSharpAsync(
            @"
<System.Reflection.DefaultMember(""Value"")>
Public Interface IWithDefaultValue
    Property Value As Object
End Interface

Public Module Module1
    Sub Test(p As IWithDefaultValue)
        Dim v = p.Value
        p.Value = v
    End Sub
End Module", @"using System.Reflection;

[DefaultMember(""Value"")]
public partial interface IWithDefaultValue
{
    object Value { get; set; }
}

public static partial class Module1
{
    public static void Test(IWithDefaultValue p)
    {
        var v = p.Value;
        p.Value = v;
    }
}
");
    }
}
