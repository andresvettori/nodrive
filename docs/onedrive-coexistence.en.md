# Using NoDrive with OneDrive

NoDrive and the official OneDrive app can run on the same PC and the same personal account. They behave like two computers: a change made in one reaches the other through the cloud. They use different folders, so never place the NoDrive folder inside the OneDrive folder.

## Folder backup (Desktop, Documents, Pictures)
If OneDrive backs up these folders, they live inside the OneDrive folder and OneDrive keeps managing them. NoDrive also shows their files in its own folder. Nothing is moved or changed by NoDrive.

To let NoDrive take over, first stop the backup in OneDrive: OneDrive icon > Settings > Sync and backup > Manage backup > turn off the folders. OneDrive then offers to keep the files in the old folders. Choose to keep them there, confirm they are complete, then continue using NoDrive.

## Before you stop Folder Backup
1. In OneDrive: icon > Settings > Sync and backup > Advanced settings > Files On-Demand > **Download all files** for the folders you back up, and wait for the green checks. Stopping the backup fails for online-only files.
2. After stopping the backup, check the folders returned to your profile: in PowerShell, `[Environment]::GetFolderPath('Desktop')`, `('MyDocuments')` and `('MyPictures')` should print `C:\Users\<you>\...`, not a OneDrive path. If not, right-click the folder in Explorer > Properties > Location > **Restore Default**.
3. When OneDrive asks, choose to keep the files in their original folders on this PC (do not choose to remove them from the PC or from OneDrive).

## Switching over completely
1. Make sure NoDrive shows all the files you need (open a few, or use Always keep on this device for important folders).
2. Stop folder backup as above.
3. Quit OneDrive and, once you are happy, unlink the account in OneDrive settings.

Do this gradually: nothing in NoDrive deletes data without going to the OneDrive recycle bin.

The settings page has an optional **Switch from OneDrive** tab that checks your folder against the cloud (read-only), walks you through the OneDrive steps, and lets NoDrive take over the folder. It never runs unless you open it.
