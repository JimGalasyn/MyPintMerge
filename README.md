# MyPintMerge
Cherry-pick a commit automatically across multiple branches.

Use the `MyPintMerge` command-line utility to automate cherry-picking a commit
from one branch to other branches. It's a .NET Core app, so it runs on Windows,
macOS, and Linux.

## Usage

To run `MyPintMerge`, specify the following arguments:

- A base name for the local branches
- The name of the remote branch that has the commit
- The commit SHA
- Your authentication credentials to the git repo

Run `MyPintMerge` on the command-line in Windows, macOS, or Linux:

```bash
MyPintMerge <local-branch-base-name> <remote-branch-name> <commit-sha> <login> <password>
```

Specify the branches that receive the commit in appsettings.json, in the 
`repoManagement` section:

```json
  "repoManagement": {
    "remoteRepoName": "upstream",
    "repoLocalPath": "C:\\Users\\JimGalasyn\\Source\\Repos\\ksql",
    "availableBranches": [
      "master",
      "0.9.0-ksqldb",
      "0.9.x-ksqldb",
      "0.8.1-ksqldb",
      "0.7.1-ksqldb"
    ],
    "sigName": "Jim Galasyn",
    "sigEmail": "jim.galasyn@confluent.io"
  },
```

Also in the `repoManagement` section are settings for the remote repo name
and the path to your local clone of the repo. 

`MyPintMerge` implements the following workflow:

1. Validate command-line arguments.
2. Fetch the state of the remote repo.
3. Get the source branch in the remote repo to cherry-pick from.
4. Find the specified commit in the source branch.
5. Get the names of the branches that will get the commit.
6. Iterate through the branches. For each branch:
   1. Create and check out a local branch.
   2. Cherry-pick the commit to the local branch.
   3. Push the local branch to the tracked remote branch.
   4. Optionally delete the local branch. Set `deleteLocalBranches`
      in the `appConfig` section of appsettings.json.

`MyPintMerge` iterates through the list of available remote branches and
creates a corresponding local branch for each remote branch. This operation
is the equivalent of the following command-line:

```bash
git checkout -b <local-branch-name> -t <remote-repo-name>/<tracked-branch-name>
```

For example, the following command creates a local branch
named `docs-2358` which tracks the remote branch named `0.8.1-ksqldb`
in the remote repo named `upstream`:

```bash
git checkout -b docs-2358 -t upstream/0.8.1-ksqldb
```

`MyPintMerge` names a local branch by appending the remote branch name to the
provided base name. For example, if the base name is `docs-2358` and the
tracked remote branch is named `0.8.1-ksqldb`, the local branch name is
`docs-2358-0.8.1-ksqldb`.

If the cherry-pick causes a merge conflict, `MyPintMerge` exits immediately
without changing any more state.

If no change to the branch is detected after the cherry-pick, which means that
the commit is already present in the branch, `MyPintMerge` skips to the next
branch.

## Implementation

`MyPintMerge` is implemented as a .NET Core console application. It uses the
[libgit2sharp NuGet package](https://github.com/libgit2/libgit2sharp/) to
interface with git.

## Contact
For questions and feedback, please contact me at @JimGalasyn.
