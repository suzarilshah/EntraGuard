-- Export only the supplied deck. Existing user documents are not modified or closed.
on run argv
    set sourceFile to POSIX file (item 1 of argv)
    set targetFile to POSIX file (item 2 of argv)
    with timeout of 180 seconds
        tell application "Keynote"
            set presentationDocument to open sourceFile
            export presentationDocument to targetFile as PDF
            close presentationDocument saving no
        end tell
    end timeout
end run
