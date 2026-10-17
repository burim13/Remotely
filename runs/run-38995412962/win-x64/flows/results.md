### UI flows

| Check | Result | Detail |
|---|---|---|
| flow first-run onboarding | pass | first-run flow shown, step text "Step 1 of 4"; step 2 shows: Share this PC on your network?; step 3 shows: Connect to your server (optional); step 4 summary mentions the chosen name: "This PC is called UITest-PC."; after Finish the Home page with the Share switch is shown; viewer.json has OnboardingDone = true; viewer.json has the typed PC name |
| flow home: wrong token, share this PC, settings | pass | typing an incomplete token shows the hint "3 of 12 characters."; a wrong token ends with a readable error: "No PC on this network is showing a token starting ZZZZ. Check the token, that the other PC is showing it, and that both PCs are on the same network."; sharing on shows a token and Copy puts it on the clipboard in the form XXXX-XXXX-XXXX (starts H8H5); state line while sharing: "Ready. Another PC on this network can connect with the token below."; switching off shows "Not sharing. Nobody can connect to this PC."; the Settings page opens (found its Appearance / PC name field) |
