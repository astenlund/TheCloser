namespace TheCloser
{
    public class Program
    {
        public static void Main()
        {
            NativeMethods.GetProcessFromMouseCursorPosition().Kill();
        }
    }
}
