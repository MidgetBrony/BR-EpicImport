using BREpicImport;

Console.Title = "BOXROOM Epic Importer - Experimental";
Console.ForegroundColor = ConsoleColor.Magenta;
Console.WriteLine("EXPERIMENTAL EPIC GAMES IMPORTER");
Console.ResetColor();
Console.WriteLine("This tool uses Epic's launcher services and may break if Epic changes them.");
Console.WriteLine();
Console.WriteLine("If Epic asks you to sign in, the browser will finish on a JSON page.");
Console.WriteLine("Copy the authorizationCode value (or the complete JSON) back into this window.");
Console.WriteLine();
Console.Write("Press Enter to continue...");
Console.ReadLine();

try
{
    using var epic = new EpicClient();
    await epic.InitializeAsync();
    await epic.ImportOwnedGamesAsync();
}
catch (Exception exception)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine();
    Console.WriteLine($"Import failed: {exception.Message}");
    Console.ResetColor();
}

Console.WriteLine();
Console.WriteLine("Press any key to close.");
Console.ReadKey();

