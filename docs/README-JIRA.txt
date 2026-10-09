Guides for Jira
===============

Espanso-Guide.md / .jira.txt         full guide
Espanso-Guide-Short.md / .jira.txt   short guide for colleagues
images\                              mockup screenshots used by both

Confluence Cloud (easiest)
  Use ConfluencePublisher.exe from https://github.com/csabakovacs314/confluence-publisher :
  open a guide's .md file, sign in, pick the parent page and press Publish.
  It asks you to confirm the parent page and whether it creates or updates a page before writing anything,
  and attaches the pictures for you.

Jira Cloud
  1. Paste the .md file's text into the description.
  2. Drag the PNG files from images\ to where each picture goes.

Jira Server or Data Center
  1. Attach the PNG files from images\ to the issue.
  2. Paste the .jira.txt file's text into the Text (wiki markup) tab.
     The pictures show by themselves, because they refer to the attachments by name.

To make the .jira.txt files again after editing a .md file (needs Python 3), use md2jira.py from
confluence-publisher:
  py <confluence-publisher>\tools\md2jira.py docs\Espanso-Guide.md docs\Espanso-Guide-Short.md

The screenshots are mockups drawn from docs\mockups\mockup.html, in the app's own colours.
