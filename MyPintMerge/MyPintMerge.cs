using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;

using LibGit2Sharp;
using LibGit2Sharp.Handlers;

/// <summary>
/// Poor-man's pint merge.
/// </summary>
/// <remarks>
/// LibGit2Sharp docs: https://github.com/libgit2/libgit2sharp/wiki/LibGit2Sharp-Hitchhiker%27s-Guide-to-Git
/// </remarks>
namespace MPM
{
    /// <summary>
    /// Cherry-picks a commit to the specified branches.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>0 on success, -1 on failure</returns>
    /// <remarks><para>
    /// <see cref="MyPintMerge"/> implements a workflow that's similar to
    /// the pint merge utility, but for arbitrary branches, which are listed
    /// in the <see cref="AvailableBranches"/> property.</para>
    /// <para>
    /// The workflow has these steps:
    /// <list type="number">
    /// <item>Validate command-line arguments.</item>
    /// <item>Fetch the state of the upstream remote repo.</item>
    /// <item>Get the source branch in the remote repo to cherry-pick from.</item>
    /// <item>Find the specified commit in the source branch.</item>
    /// <item>Get the names of the branches that will get the commit.</item>
    /// <item>Iterate through the branches. For each branch:</item>
    /// <item>Create and check out a local branch.</item>
    /// <item>Cherry-pick the commit to the local branch.</item>
    /// <item>Push the local branch to the tracked remote branch.</item>
    /// </list></para>
    /// <para><see cref="MyPintMerge"/> was written by Jim Galasyn.
    /// Please feel free to contact me at jim.galasyn@confluent.io.</para>
    /// <para>Configuration is specified in the appsettings.json file.
    /// </para>
    /// </remarks>
    class MyPintMerge
    {
        static int Main(string[] args)
        {
            // Get configration settings from the appsettings.json file.
            ConfigureApplication();

            // Validate and cache arguments.
            if (!CheckArguments(args))
            {
                Console.WriteLine(Usage);
                return ErrorCode;
            }

            // Create a Repository instance on the local path to the git repo.
            using (var repo = new Repository(RepoLocalPath))
            {
                // Fetch the state of the remote repo, which is typically named "upstream".
                PrintMessage($"Fetching state of {RemoteRepoName}");
                var remote = repo.Network.Remotes[RemoteRepoName];
                var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
                string logMessage = "";
                Commands.Fetch(repo, remote.Name, refSpecs, null, logMessage);

                // Get the branch to cherry-pick from.
                var sourceBranch = repo.Branches[SourceBranchName];
                if (sourceBranch == null)
                {
                    // Repository returns a null object when the requested branch doesn't exist.
                    PrintMessage($"Source branch {SourceBranchName} not found in {RemoteRepoName}, exiting.");
                    return ErrorCode;
                }
                else
                {
                    PrintMessage($"Found branch {sourceBranch.FriendlyName} in {RemoteRepoName}");
                }

                // Find the commit in the git log of the source branch.
                var sourceCommit = sourceBranch.Commits.FirstOrDefault(c => c.Sha == CommitSha);
                if (sourceCommit == null)
                {
                    PrintMessage($"Commit {CommitSha} not found in {sourceBranch.FriendlyName}, no action taken, exiting.");
                    return ErrorCode;
                }

                // Get the branches to merge to, which is a list of the available
                // branches minus the source branch for the commit.
                var branchesToMerge = AvailableBranches.Where(b => b != SourceBranchName);

                // Assign cherry-pick options.
                CherryPickOptions options = CreateCherryPickOptions();

                // Set up the signature for the cherry-pick message.
                Signature sig = new Signature(SigName, SigEmail, DateTime.Now);

                // Create local branches that track the available remote branches.
                // In each local branch, do the cherry-pick and push to the remote repo.
                PrintMessage($"Cherry-picking from {sourceBranch.FriendlyName} to available branches.");
                foreach (var trackedBranchName in branchesToMerge)
                {
                    // Create and check out the local branch, which is equivalent to the command:
                    //
                    // git checkout -b <local-branch-name> -t <remote-repo-name>/<tracked-branch-name>
                    //
                    // For example, the following command creates a local branch
                    // named "docs-2358" which tracks the remote branch named "0.8.1-ksqldb"
                    // in the remote repo named "upstream":
                    //
                    // git checkout -b docs-2358 -t upstream/0.8.1-ksqldb

                    // Name the local branch by appending the remote branch name to the provided base name.
                    // For example, if the base name is "docs-2358" and the tracked branch is named
                    // "0.8.1-ksqldb", the local branch name is "docs-2358-0.8.1-ksqldb".
                    string localBranchName = $"{LocalBranchBaseName}-{trackedBranchName}";

                    // For future reference, this is the "objectish" representation:
                    // string objectish = string.Format(CultureInfo.InvariantCulture, "{0}:{1}", localBranch.CanonicalName, localBranch.UpstreamBranchCanonicalName);

                    // Get a reference on the remote tracking branch.
                    Branch trackedBranch = repo.Branches[$"{RemoteRepoName}/{trackedBranchName}"];

                    // If a local branch with the same name exists, probably from a
                    // previous MyPintMerge session, delete it.
                    if (repo.Branches.Any(b => b.FriendlyName == localBranchName))
                    {
                        DeleteBranch(repo, repo.Branches[localBranchName]);
                    }

                    // Create and check out the local branch.
                    PrintMessage($"Checking out local branch {localBranchName} tracking remote branch {trackedBranch.FriendlyName}");
                    Branch localBranch = repo.CreateBranch(localBranchName, trackedBranch.Tip);
                    Branch updatedBranch = repo.Branches.Update(
                        localBranch,
                        b => b.TrackedBranch = trackedBranch.CanonicalName);
                    CheckoutOptions checkoutOptions = CreateCheckoutOptions();
                    checkoutCounter = 0;
                    Commands.Checkout(repo, localBranch, checkoutOptions);

                    // Cherry-pick to the currently checked out branch.
                    PrintMessage($"Cherry-picking commit {sourceCommit.Sha} to local branch {updatedBranch.FriendlyName}");
                    try
                    {
                        var pickResult = repo.CherryPick(sourceCommit, sig, options);

                        // Check the return value from the CherryPick method,
                        // which can fail without throwing or sending a notification
                        // to the callbacks.
                        if (pickResult.Status == CherryPickStatus.Conflicts)
                        {
                            // If there are merge conflcts, exit.
                            PrintMessage($"CONFLICT in local branch {updatedBranch.FriendlyName}, exiting.");
                            return ErrorCode;
                        }
                    }
                    catch (EmptyCommitException ecex)
                    {
                        // Oddly, when there's nothing to do, i.e., when the commit
                        // exists already in the tracked branch, libgit2sharp
                        // throws an EmptyCommitException instead of reutrning
                        // a CherryPickResult.
                        PrintMessage($"No changes detected, no action taken in local branch {updatedBranch.FriendlyName}, continuing.");

                        if (DeleteLocalBranches)
                        {
                            DeleteBranch(repo, localBranch);
                        }

                        continue;
                    }
                    catch (Exception ex)
                    {
                        PrintMessage($"Exception during cherry-pick {ex}, no action taken in local branch {updatedBranch.FriendlyName}, continuing.");

                        //if (DeleteLocalBranches)
                        //{
                        //    DeleteBranch(repo, localBranch);
                        //}

                        continue;
                    }

                    // Prepare to push the changes to the tracked remote branch.
                    // Assign the configuration options for the push.
                    PushOptions pushOptions = CreatePushOptions();

                    // Push the branch to the remote repo.
                    PrintMessage($"Pushing local branch {localBranchName} to remote {trackedBranch.FriendlyName}");
                    repo.Network.Push(localBranch, pushOptions);

                    // Optionally, clean up by deleting the local branch.
                    if (DeleteLocalBranches)
                    {
                        DeleteBranch(repo, localBranch);
                    }
                }
            }

            return SuccessCode;
        }

        #region Implementation

        /// <summary>
        /// Gets configration settings from the appsettings.json file.
        /// </summary>
        /// <returns>true</returns>
        private static bool ConfigureApplication()
        {
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            // Application configuration settings.
            Usage = configuration[UsageSettingName];
            DefaultBranchName = configuration[DefaultBranchSettingName];
            DeleteLocalBranches = bool.Parse(configuration[DeleteLocalBranchesSettingName]);
            SuccessCode = int.Parse(configuration[SuccessCodeSettingName]);
            ErrorCode = int.Parse(configuration[ErrorCodeSettingName]);

            // Repository management settings.
            RemoteRepoName = configuration[RemoteRepoNameSettingName];
            RepoLocalPath = configuration[RepoLocalPathSettingName];
            AvailableBranches = configuration.GetSection(AvailableBranchesSettingName).Get<List<string>>();
            SigName = configuration[SigNameSettingName];
            SigEmail = configuration[SigEmailSettingName];

            // Argument validation settings.
            NumRequiredArgs = int.Parse(configuration[NumRequiredArgsSettingName]);
            CommitShaLength = int.Parse(configuration[CommitShaLengthSettingName]);
            LocalBranchNameArgIndex = int.Parse(configuration[LocalBranchNameArgIndexSettingName]);
            SourceBranchNameArgIndex = int.Parse(configuration[SourceBranchNameArgIndexSettingName]);
            CommitShaArgIndex = int.Parse(configuration[CommitShaArgIndexSettingName]);
            LoginArgIndex = int.Parse(configuration[LoginArgIndexSettingName]);
            PasswordArgIndex = int.Parse(configuration[PasswordArgIndexSettingName]);

            return true;
        }

        /// <summary>
        /// Creates and populates a new <see cref="CheckoutOptions"/> object.
        /// </summary>
        /// <returns>Settings for a <see cref="Commands.Checkout(IRepository, Branch, CheckoutOptions)"/>
        /// command.</returns>
        /// <remarks>The <see cref="CheckoutModifiers.Force"/> flag is set to force the
        /// checkout, discarding any changes in the current local branch.
        /// </remarks>
        private static CheckoutOptions CreateCheckoutOptions()
        {
            return new CheckoutOptions
            {
                CheckoutNotifyFlags = CheckoutNotifyFlags.Conflict | CheckoutNotifyFlags.Dirty | CheckoutNotifyFlags.Updated | CheckoutNotifyFlags.Untracked,
                CheckoutModifiers = CheckoutModifiers.Force,
                OnCheckoutNotify = new CheckoutNotifyHandler(HandleCheckoutNotifications),
                OnCheckoutProgress = new CheckoutProgressHandler(HandleCheckoutProgress)
            };
        }


        /// <summary>
        /// Creates and populates a new <see cref="CherryPickOptions"/> object.
        /// </summary>
        /// <returns>Settings for a <see cref="Repository.CherryPick(Commit, Signature, CherryPickOptions)"/>
        /// command.</returns>
        /// <remarks>Sets up the callback for events that occur during a cherry-pick operation.
        /// Merge conflicts cause the cherry-pick operation to fail.
        /// </remarks>
        private static CherryPickOptions CreateCherryPickOptions()
        {
            return new CherryPickOptions
            {
                CommitOnSuccess = true, // Commit to the local branch if the cherry-pick succeeds.
                FailOnConflict = true, // Fail the cherry-pick causes a merge conflict.
                //FileConflictStrategy = CheckoutFileConflictStrategy.Theirs,
                IgnoreWhitespaceChange = true,
                CheckoutNotifyFlags = CheckoutNotifyFlags.Conflict | CheckoutNotifyFlags.Dirty | CheckoutNotifyFlags.Updated | CheckoutNotifyFlags.Untracked,
                OnCheckoutNotify = new CheckoutNotifyHandler(HandleCheckoutNotifications),
                OnCheckoutProgress = new CheckoutProgressHandler(HandleCheckoutProgress)
            };
        }

        private static PushOptions CreatePushOptions()
        {
            return new PushOptions
            {
                CredentialsProvider = new CredentialsHandler(HandleCredentials),
                OnNegotiationCompletedBeforePush = new PrePushHandler(HandlePrePush),
                OnPushTransferProgress = new PushTransferProgressHandler(HandlePushProgress),
                OnPushStatusError = new PushStatusErrorHandler(HandlePushError),
                OnPackBuilderProgress = new PackBuilderProgressHandler(HandlePackBuilderProgress),
                CertificateCheck = new CertificateCheckHandler(HandleCertificateCheck)
            };
        }

        /// <summary>
        /// Implements the <see cref="PushOptions.CredentialsProvider"/> callback.
        /// </summary>
        /// <param name="url"></param>
        /// <param name="usernameFromUrl"></param>
        /// <param name="types"></param>
        /// <returns>Credentials for authenticating to a remote git repo.</returns>
        private static Credentials HandleCredentials(string url, string usernameFromUrl, SupportedCredentialTypes types)
        {
            return new UsernamePasswordCredentials()
            {
                Username = Login,
                Password = Token
            };
        }

        /// <summary>
        /// Implements the <see cref="PushOptions.OnPushTransferProgress"/> callback.
        /// </summary>
        /// <param name="current">The number of objects that have been transmitted.</param>
        /// <param name="total">The number of objects to transmit.</param>
        /// <param name="bytes">The number of bytes that have been transmitted.</param>
        /// <returns>true</returns>
        private static bool HandlePushProgress(int current, int total, long bytes)
        {
            PrintMessage($"Pushing changes: {current}/{total} objects, {bytes} bytes");
            return true;
        }

        /// <summary>
        /// Implements the <see cref="PushOptions.OnPushStatusError"/> callback.
        /// </summary>
        /// <param name="error">The error message.</param>
        private static void HandlePushError(PushStatusError error)
        {
            PrintMessage(error.Message);
        }

        /// <summary>
        /// Implements the <see cref="PushOptions.OnNegotiationCompletedBeforePush"/> callback.
        /// </summary>
        /// <param name="updates">A collection of updated files to push.</param>
        /// <returns>true</returns>
        private static bool HandlePrePush(IEnumerable<PushUpdate> updates)
        {
            foreach (var update in updates)
            {
                PrintMessage($"{update}");
            }

            return true;
        }

        /// <summary>
        /// Implements the <see cref="PushOptions.OnPackBuilderProgress"/> callback.
        /// </summary>
        /// <param name="stage"></param>
        /// <param name="current"></param>
        /// <param name="total"></param>
        /// <returns>true</returns>
        private static bool HandlePackBuilderProgress(PackBuilderStage stage, int current, int total)
        {
            PrintMessage($"Pack builder {stage}: {current} {total}");

            return true;
        }

        /// <summary>
        /// Implements the <see cref="PushOptions.CertificateCheck"/> callback.
        /// </summary>
        /// <param name="cert">The <see cref="Certificate"/> that was tried.</param>
        /// <param name="valid">true is the provided <see cref="Certificate"/> is valid
        /// for <paramref name="host"/>; otherwise, false.</param>
        /// <param name="host">The host name that <paramref name="cert"/> was checked against.</param>
        /// <returns></returns>
        private static bool HandleCertificateCheck(Certificate cert, bool valid, string host)
        {
            string isValid = valid ? "valid" : "NOT VALID";
            PrintMessage($"CertificateCheck {cert}: is {isValid} for host {host}");

            return true;
        }

        /// <summary>
        /// Implements the <see cref="CheckoutOptions.OnCheckoutNotify"/> callback.
        /// </summary>
        /// <param name="path">The path to the file</param>
        /// <param name="flags">Flags for setting which notifications to receive.</param>
        /// <returns></returns>
        private static bool HandleCheckoutNotifications(string path, CheckoutNotifyFlags flags)
        {
            switch (flags)
            {
                case CheckoutNotifyFlags.Conflict:
                    PrintMessage("conflicted file found at " + path);
                    return true;
                case CheckoutNotifyFlags.Ignored:
                    PrintMessage("ignored file found at: " + path);
                    return true;
                case CheckoutNotifyFlags.None:
                    PrintMessage("unchanged file found at: " + path);
                    return true;
                case CheckoutNotifyFlags.Untracked:
                    PrintMessage("untracked file found at: " + path);
                    return true;
                case CheckoutNotifyFlags.Updated:
                    //PrintMessage("updated file found at: " + path);
                    return true;
                default:
                    PrintMessage("dirty file at: " + path);
                    return true;
            }
        }

        /// <summary>
        /// Implements the <see cref="CheckoutOptions.OnCheckoutProgress"/> callback.
        /// </summary>
        /// <param name="path">The path to an updated file.</param>
        /// <param name="completedSteps">The number of checkout steps (files) that have succeeded.</param>
        /// <param name="totalSteps">The total number of steps (files) to sync.</param>
        /// <remarks>Because <paramref name="totalSteps"/> can number in the thousands,
        /// the output is throttled by <see cref="checkoutCounter"/>.
        /// TODO: move the numeric literals to configuration.</remarks>
        private static void HandleCheckoutProgress(string path, int completedSteps, int totalSteps)
        {
            int reportProgressDelta = 1;

            if (totalSteps > 1000)
            {
                reportProgressDelta = 100;
            }
            else if (totalSteps > 100)
            {
                reportProgressDelta = 10;
            }

            if (checkoutCounter == 0)
            {
                PrintMessage($"Checkout progress: {completedSteps}/{totalSteps} {path} ");
            }

            checkoutCounter = (checkoutCounter + 1) % reportProgressDelta;
        }

        #endregion

        #region Utility methods

        /// <summary>
        /// Validates the command-line arguments.
        /// </summary>
        /// <param name="args">The command-line arguments to validate.</param>
        /// <returns>true if arguments are valid; otherwise, false.</returns>
        /// <remarks><see cref="MyPintMerge"/> expects exactly the number of
        /// command-line arguments specified in <see cref="NumRequiredArgs"/>.
        /// </remarks>
        private static bool CheckArguments(string[] args)
        {
            bool argsAreValid = false;

            if (args != null)
            {
                if (args.Length == NumRequiredArgs)
                {
                    var missingArgs = args.Where(a => string.IsNullOrEmpty(a));
                    if (missingArgs.Count() == 0)
                    {
                        string localBranchArg = args[LocalBranchNameArgIndex];
                        string sourceBranchArg = args[SourceBranchNameArgIndex];
                        string commmitShaArg = args[CommitShaArgIndex];
                        string login = args[LoginArgIndex];
                        string password = args[PasswordArgIndex];

                        if (AvailableBranches.Contains(sourceBranchArg))
                        {
                            if (commmitShaArg.Length == CommitShaLength)
                            {
                                LocalBranchBaseName = localBranchArg;
                                SourceBranchName = sourceBranchArg;
                                CommitSha = commmitShaArg;
                                Login = login;
                                Token = password;
                                argsAreValid = true;
                            }
                        }
                    }
                }
            }

            return argsAreValid;
        }

        /// <summary>
        /// Deletes the specified branch.
        /// </summary>
        /// <param name="repo">the <see cref="Repository"/> to delete <paramref name="branch"/> from.</param>
        /// <param name="branch">the branch to delete.</param>
        /// <remarks>Implements the equivalent of these git commands:
        /// <code>
        /// git checkout -f master
        /// git branch -D <branch-name>
        ///</code>
        /// TODO: Handle CheckoutConflictException?</remarks>
        private static void DeleteBranch(Repository repo, Branch branch)
        {
            // Can throw a LibGit2Sharp.CheckoutConflictException: 'unresolved conflicts exist in the index'.
            // Checkout options are set to force the checkout, discarding
            // any changes in the current local branch.
            Commands.Checkout(repo, DefaultBranchName, CreateCheckoutOptions());

            repo.Branches.Remove(branch);
        }

        /// <summary>
        /// Outputs a message to channels like the console and the debug stream.
        /// </summary>
        /// <param name="msg">The message to output.</param>
        private static void PrintMessage(string msg)
        {
            Console.WriteLine(msg);
            Debug.WriteLine(msg);
        }

        #endregion

        #region Private Properties

        /// <summary>
        /// Gets the base name to use for naming local branches.
        /// </summary>
        /// <remarks>
        /// A unique identifier will be appended to the base name,
        /// for example the friendly name of a tracked remote branch.
        /// </remarks>
        private static string LocalBranchBaseName { get; set; }

        /// <summary>
        /// Gets the friendly name of the branch to cherry-pick from.
        /// </summary>
        private static string SourceBranchName { get; set; }

        /// <summary>
        /// Gets the 40-character commit SHA for the commit to cherry-pick.
        /// </summary>
        private static string CommitSha { get; set; }

        /// <summary>
        /// Gets a string that represents the command syntax.
        /// </summary>
        private static string Usage { get; set; }

        /// <summary>
        /// Gets the friendly name of the branch to use as the default.
        /// </summary>
        /// <remarks>The default branch name is usually "master".</remarks>
        private static string DefaultBranchName { get; set; }

        /// <summary>
        /// Gets a value indicating whether local branches should be
        /// when they're no longer need by <see cref="MyPintMerge"/>.
        /// </summary>
        private static bool DeleteLocalBranches { get; set; }

        /// <summary>
        /// Get the name of the remote repo.
        /// </summary>
        /// <remarks>
        /// As reported by the <code>git remote -v</code> command.
        /// The typical name is "upstream". 
        /// </remarks>
        private static string RemoteRepoName { get; set; }

        /// <summary>
        /// Gets the file system path to the local clone of the remote repo.
        /// </summary>
        private static string RepoLocalPath { get; set; }

        /// <summary>
        /// Gets the branches in the remote repo that can be merged to.
        /// </summary>
        private static List<string> AvailableBranches { get; set; }

        /// <summary>
        /// Gets a user name for signing the cherry-picked commit.
        /// </summary>
        /// <remarks>Used in the <see cref="Signature"/> class</remarks>
        private static string SigName { get; set; }

        /// <summary>
        /// Gets an email for signing the cherry-picked commit.
        /// </summary>
        /// <remarks>Used in the <see cref="Signature"/> class</remarks>
        private static string SigEmail { get; set; }

        /// <summary>
        /// Gets a user login for authenticating to the remote repo.
        /// </summary>
        /// <remarks>
        /// This should not be hard-coded. It should be extracted
        /// from .netrc or a credential cache.
        /// </remarks>
        private static string Login { get; set; }

        /// <summary>
        /// Gets a password or a personal authentication token for
        /// authenticating to the remote repo.
        /// </summary>
        /// <remarks>
        /// This should not be hard-coded. It should be extracted
        /// from .netrc or a credential cache.
        /// </remarks>
        private static string Token { get; set; }

        /// <summary>
        /// Gets the number of expected command-line arguments.
        /// </summary>
        private static int NumRequiredArgs { get; set; }

        /// <summary>
        /// Gets the length of a commit SHA.
        /// </summary>
        /// <remarks> Always returns 40.</remarks>
        private static int CommitShaLength { get; set; }

        /// <summary>
        /// Gets the position in the argument list of the base name for
        /// the local branches.
        /// </summary>
        private static int LocalBranchNameArgIndex { get; set; }

        /// <summary>
        /// Gets the position in the argument list of the source branch
        /// for the commit to be cherry-picked.
        /// </summary>
        private static int SourceBranchNameArgIndex { get; set; }

        /// <summary>
        /// Gets the position in the argument list of the commit SHA that
        /// identifies the commit in the source branch.
        /// </summary>
        private static int CommitShaArgIndex { get; set; }

        /// <summary>
        /// Gets the position in the argument list of the user name for
        /// authenticating to the remote repo.
        /// </summary>
        private static int LoginArgIndex { get; set; }

        /// Gets the position in the argument list of the password or
        /// personal authentication token for authenticating to the remote repo.
        private static int PasswordArgIndex { get; set; }

        /// <summary>
        /// Gets the return code that indicates successful completion.
        /// </summary>
        private static int SuccessCode { get; set; }

        /// <summary>
        /// Gets the return code that indicates an error condition occurred.
        /// </summary>
        private static int ErrorCode { get; set; }

        #endregion

        #region Names for configuration settings

        // The following properties assemble the name of configuration values
        // in the appsettings.json file. They use the following schema:
        // <config-section>:<setting-name>. 
        //
        // For example, the "returnCodes" section has two settings, "successCode"
        // and "errorCode":
        //  
        //  "returnCodes": {
        //    "successCode": 0,
        //    "errorCode": -1
        //
        // To get the config value for success, pass the string
        // "returnCodes:successCode", which is returned by the 
        // SuccessCodeSettingName property, to the configration root:
        // 
        // SuccessCode = int.Parse(configuration[SuccessCodeSettingName]);
        // 
        // See the ConfigureApplication method for usage.

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="Usage"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string UsageSettingName
        {
            get
            {
                return $"{appConfigSettingsName}:{usageSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="DefaultBranchName"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string DefaultBranchSettingName
        {
            get
            {
                return $"{appConfigSettingsName}:{defaultBranchSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="DeleteLocalBranches"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string DeleteLocalBranchesSettingName
        {
            get
            {
                return $"{appConfigSettingsName}:{deleteLocalBranchesSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="SuccessCode"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string SuccessCodeSettingName
        {
            get
            {
                return $"{returnCodeSettingsName}:{successCodeSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="ErrorCode"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string ErrorCodeSettingName
        {
            get
            {
                return $"{returnCodeSettingsName}:{errorCodeSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="NumRequiredArgs"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string NumRequiredArgsSettingName
        {
            get
            {
                return $"{argValidationSettingsName}:{numRequiredArgsSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="CommitShaLength"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string CommitShaLengthSettingName
        {
            get
            {
                return $"{argValidationSettingsName}:{commitShaLengthSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="LocalBranchNameArgIndex"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string LocalBranchNameArgIndexSettingName
        {
            get
            {
                return $"{argValidationSettingsName}:{localBranchNameArgIndexSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="SourceBranchNameArgIndex"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string SourceBranchNameArgIndexSettingName
        {
            get
            {
                return $"{argValidationSettingsName}:{sourceBranchNameArgIndexSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="CommitShaArgIndex"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string CommitShaArgIndexSettingName
        {
            get
            {
                return $"{argValidationSettingsName}:{commitShaArgIndexSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="LoginArgIndex"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string LoginArgIndexSettingName
        {
            get
            {
                return $"{argValidationSettingsName}:{loginArgIndexSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="PasswordArgIndex"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string PasswordArgIndexSettingName
        {
            get
            {
                return $"{argValidationSettingsName}:{passwordArgIndexSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="RemoteRepoName"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string RemoteRepoNameSettingName
        {
            get
            {
                return $"{repoManagementSettingsName}:{remoteRepoNameSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="RepoLocalPath"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string RepoLocalPathSettingName
        {
            get
            {
                return $"{repoManagementSettingsName}:{repoLocalPathSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="AvailableBranches"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string AvailableBranchesSettingName
        {
            get
            {
                return $"{repoManagementSettingsName}:{availableBranchesSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="SigName"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string SigNameSettingName
        {
            get
            {
                return $"{repoManagementSettingsName}:{sigNameSettingName}";
            }
        }

        /// <summary>
        /// Gets the name of the configuration setting for the <see cref="SigEmail"/>
        /// property.
        /// </summary>
        /// <remarks>See the <see cref="ConfigureApplication"/> method for usage.</remarks>
        private static string SigEmailSettingName
        {
            get
            {
                return $"{repoManagementSettingsName}:{sigEmailSettingName}";
            }
        }

        #endregion

        #region Private fields

        // Names for repository-related settings in appsettings.json.
        private const string repoManagementSettingsName = "repoManagement";
        private const string remoteRepoNameSettingName = "remoteRepoName";
        private const string repoLocalPathSettingName = "repoLocalPath";
        private const string availableBranchesSettingName = "availableBranches";
        private const string sigNameSettingName = "sigName";
        private const string sigEmailSettingName = "sigEmail";

        // Names for app configuration settings in appsettings.json.
        private const string appConfigSettingsName = "appConfig";
        private const string usageSettingName = "usage";
        private const string defaultBranchSettingName = "defaultBranch";
        private const string deleteLocalBranchesSettingName = "deleteLocalBranches";

        // Names for return code settings in appsettings.json.
        private const string returnCodeSettingsName = "returnCodes";
        private const string successCodeSettingName = "successCode";
        private const string errorCodeSettingName = "errorCode";

        // Names for argument validation settings in appsettings.json.
        private const string argValidationSettingsName = "argValidation";
        private const string numRequiredArgsSettingName = "numRequiredArgs";
        private const string commitShaLengthSettingName = "commitShaLength";
        private const string localBranchNameArgIndexSettingName = "localBranchNameArgIndex";
        private const string sourceBranchNameArgIndexSettingName = "sourceBranchNameArgIndex";
        private const string commitShaArgIndexSettingName = "commitShaArgIndex";
        private const string loginArgIndexSettingName = "loginArgIndex";
        private const string passwordArgIndexSettingName = "passwordArgIndex";

        // Counter for throttling notifications.
        private static int checkoutCounter = 0;

        #endregion
    }
}
