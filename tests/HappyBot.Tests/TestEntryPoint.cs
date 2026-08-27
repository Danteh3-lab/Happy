using System.Reflection;

internal static class TestEntryPoint
{
    private static int Main()
    {
        try
        {
            LiveSettingsRegression.VerifyLiveSwitchCopy();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        MethodInfo main = typeof(Program).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(Program).FullName, "Main");

        try
        {
            return (int)(main.Invoke(null, null) ?? 1);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            Console.Error.WriteLine(ex.InnerException.Message);
            return 1;
        }
    }
}
