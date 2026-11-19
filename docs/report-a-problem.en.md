# Report a problem

If NoDrive does something wrong, a small report lets us find the cause quickly. It takes one minute.

1. Right-click the NoDrive tray icon and choose **Report a problem...** (or open NoDrive and go to the **Help** tab).
2. Leave **Include file names** ticked unless the names are private. Without names the report is harder to read.
3. Press **Create report**. NoDrive creates a zip file on your Desktop, named like `NoDrive-report-20261120-101500-a1b2c3.zip`, and opens the folder.
4. Write down what you were doing when it went wrong (for example "I renamed a folder and it showed an error").
5. Send the zip and your note to the person who gave you NoDrive.

What is in the zip: a health report, the last 7 days of NoDrive logs, the list of recent transfers, your NoDrive settings and Windows' own cloud-files log. It never contains your sign-in token, passwords or the content of your files. File names appear in the logs and the list of transfers; you can open the zip first and look.

Without the tray icon: open PowerShell and run `& "$env:LOCALAPPDATA\Programs\NoDrive\NoDrive.Engine.exe" --report`. `--doctor` prints the health report on screen.

The **Support ID** at the bottom of every NoDrive page also appears in the log: quote it in your message.

---
Website: <https://andresvettori.github.io/nodrive/>
