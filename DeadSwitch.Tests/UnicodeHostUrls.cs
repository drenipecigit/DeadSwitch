namespace DeadSwitch.Tests;

// Shared test data: URLs that look harmless as written, but that the connection reads as something else.
// Characters that cannot be seen on screen are written as \u codes.
public static class UnicodeHostUrls
{
    // Written with other characters, but the connection would use a forbidden name or address.
    public static TheoryData<string> Forbidden => new TheoryData<string>
    {
        // Fullwidth and uppercase forms of localhost.
        "http://ｌｏｃａｌｈｏｓｔ/",
        "http://ＬＯＣＡＬＨＯＳＴ/",
        "http://ｌｏｃａｌｈｏｓｔ。/",
        "http://ｌｏｃａｌｈｏｓｔ．/",
        "http://ｔｅｓｔ.ｌｏｃａｌｈｏｓｔ/",

        // Fullwidth digits.
        "http://１２７.０.０.１/",
        "http://１２７.０.０.１。/",
        "http://１６９.２５４.１６９.２５４/",
        "http://１０.０.０.１/",

        // Other characters that count as a dot: U+3002, U+FF0E and U+FF61.
        "http://127。0。0。1/",
        "http://127．0．0．1/",
        "http://127｡0｡0｡1/",
        "http://169。254。169。254/",
        "http://10．0．0．1/",

        // Short, decimal, hexadecimal and octal forms of 127.0.0.1, written with fullwidth characters.
        "http://１２７.１/",
        "http://２１３０７０６４３３/",
        "http://０ｘ７ｆ.０.０.１/",
        "http://０１７７.０.０.１/",

        // A soft hyphen disappears in the conversion, and what is left is a forbidden name or address.
        "http://local­host/",
        "http://１２­７.０.０.１/"
    };

    // The conversion to the name the connection uses fails. These gave 500 on /check.
    public static TheoryData<string> CannotBeConverted => new TheoryData<string>
    {
        "http://­.example.com/",
        "http://­/",
        "http://a‍b.example.com/",
        "http://‍/",
        "http://́.example.com/",
        "https://­.example.com/"
    };

    // Control: the fix must not reject every name with unusual characters.
    public static TheoryData<string> AllowedNames => new TheoryData<string>
    {
        "http://bücher.example/",
        "https://bücher.example/",
        "http://a­b.example.com/",
        "http://ｅｘａｍｐｌｅ.ｃｏｍ/"
    };
}
