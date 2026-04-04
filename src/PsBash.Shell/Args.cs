namespace PsBash.Shell;

public record ShellArgs(
    string? Command,
    bool Interactive,
    bool Login,
    bool ReadFromStdin)
{
    public static ShellArgs Parse(string[] args)
    {
        string? command = null;
        bool interactive = false;
        bool login = false;
        bool stdin = false;
        bool endOfOptions = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (endOfOptions)
                break;

            switch (args[i])
            {
                case "-c" when i + 1 < args.Length:
                    command = args[++i];
                    break;
                case "-c":
                    // -c without argument: leave command null
                    break;
                case "-i":
                    interactive = true;
                    break;
                case "--login":
                case "-l":
                    login = true;
                    break;
                case "-s":
                    stdin = true;
                    break;
                case "--":
                    endOfOptions = true;
                    break;
            }
        }

        return new ShellArgs(command, interactive, login, stdin);
    }
}
