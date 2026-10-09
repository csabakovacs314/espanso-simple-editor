namespace EspansoEditor.Tests;

internal static class Fixtures
{
    /// <summary>Same shape as a real hand-written file: header comments, Espanso's examples, "# --- Group ---" markers,
    /// multi-line blocks with trailing spaces, and no line break at the very end.</summary>
    public const string HandWritten =
        "# espanso match file\n" +
        "# yaml-language-server: $schema=https://raw.githubusercontent.com/espanso/espanso/dev/schemas/match.schema.json\n" +
        "\n" +
        "matches:\n" +
        "  # Simple text replacement\n" +
        "  - trigger: \":espanso\"\n" +
        "    replace: \"Hi there!\"\n" +
        "\n" +
        "  # Print the current date\n" +
        "  - trigger: \":date\"\n" +
        "    replace: \"{{mydate}}\"\n" +
        "    vars:\n" +
        "      - name: mydate\n" +
        "        type: date\n" +
        "        params:\n" +
        "          format: \"%m/%d/%Y\"\n" +
        "\n" +
        "  # --- Tickets ---\n" +
        "  - trigger: \"/ack\"\n" +
        "    replace: |\n" +
        "      Hello,\n" +
        "\n" +
        "      Thanks for the ticket. \n" +
        "\n" +
        "      Best,\n" +
        "      Support Team  \n" +
        "\n" +
        "  - trigger: \"/co\"\n" +
        "    replace: \"The requested change is done.\"\n" +
        "\n" +
        "  # --- Templates ---\n" +
        "  - trigger: \"/tpl\"\n" +
        "    replace: |\n" +
        "      * **Source:** Customer / Internal\n" +
        "      ### **Steps** \n" +
        "      1. First\n" +
        "\n" +
        "  # --- Signatures ---\n" +
        "  - trigger: \"/me\"\n" +
        "    replace: |\n" +
        "      Best,\n" +
        "\n" +
        "      Support Team";

    /// <summary>Espanso's own default base.yml.</summary>
    public const string EspansoDefault =
        "# espanso match file\n\n" +
        "# For a complete introduction, visit the official docs at: https://espanso.org/docs/\n\n" +
        "# You can use this file to define the base matches (aka snippets)\n" +
        "# that will be available in every application when using espanso.\n\n" +
        "# Matches are substitution rules: when you type the \"trigger\" string\n" +
        "# it gets replaced by the \"replace\" string.\n" +
        "matches:\n" +
        "  # Simple text replacement\n" +
        "  - trigger: \":espanso\"\n" +
        "    replace: \"Hi there!\"\n\n" +
        "  # NOTE: espanso uses YAML to define matches, so pay attention to the indentation!\n\n" +
        "  # But matches can also be dynamic:\n\n" +
        "  # Print the current date\n" +
        "  - trigger: \":date\"\n" +
        "    replace: \"{{mydate}}\"\n" +
        "    vars:\n" +
        "      - name: mydate\n" +
        "        type: date\n" +
        "        params:\n" +
        "          format: \"%m/%d/%Y\"\n\n" +
        "  # Print the output of a shell command\n" +
        "  - trigger: \":shell\"\n" +
        "    replace: \"{{output}}\"\n" +
        "    vars:\n" +
        "      - name: output\n" +
        "        type: shell\n" +
        "        params:\n" +
        "          cmd: \"echo 'Hello from your shell'\"\n\n" +
        "  # And much more! For more information, visit the docs: https://espanso.org/docs/\n";

    /// <summary>Things the form does not cover; they must survive untouched.</summary>
    public const string Advanced =
        "global_vars:\n" +
        "  - name: myname\n" +
        "    type: echo\n" +
        "    params:\n" +
        "      echo: Jane Doe\n" +
        "\n" +
        "matches:\n" +
        "  - trigger: \":sig\"\n" +
        "    replace: \"Regards, {{myname}}\"\n" +
        "  - trigger: \":cat\"\n" +
        "    image_path: \"$CONFIG/images/cat.png\"\n" +
        "  - regex: \":add\\\\((?P<a>.*?)\\\\)\"\n" +
        "    replace: \"{{a}}\"\n" +
        "  - trigger: \":pick\"\n" +
        "    form: \"[[choice]]\"\n" +
        "    form_fields:\n" +
        "      choice:\n" +
        "        type: choice\n" +
        "        values: [one, two]\n" +
        "  - trigger: \":yday\"\n" +
        "    replace: \"{{d}}\"\n" +
        "    vars:\n" +
        "      - name: d\n" +
        "        type: date\n" +
        "        params:\n" +
        "          format: \"%x\"\n" +
        "          offset: -86400\n" +
        "  - trigger: \":md\"\n" +
        "    markdown: \"**bold**\"\n" +
        "\n" +
        "imports:\n" +
        "  - \"../shared/other.yml\"\n";
}
