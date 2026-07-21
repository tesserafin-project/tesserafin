<h1 align="center">Tesserafin</h1>
<h3 align="center">The Free Software Media System</h3>

---

<p align="center">
<a href="LICENSE">
<img alt="GPL 2.0 License" src="https://img.shields.io/badge/license-GPL--2.0--only-blue.svg"/>
</a>
</p>

---

Tesserafin is a Free Software Media System that puts you in control of managing and streaming your media. It is an alternative to the proprietary Emby and Plex, to provide media from a dedicated server to end-user devices via multiple apps.

Tesserafin is a fork of [Jellyfin](https://github.com/jellyfin/jellyfin), which is itself descended from Emby's 3.5.2 release and ported to the .NET platform to enable full cross-platform support. Tesserafin does **not** claim product or protocol compatibility with Jellyfin; it is an independent project that keeps the "fin" in its name in recognition of that lineage. See [NOTICE](NOTICE) for the full fork attribution.

There are no strings attached, no premium licenses or features, and no hidden agendas: just a team that wants to build something better and work together to achieve it. We welcome anyone who is interested in joining us in our quest!

<strong>Something not working right?</strong><br/>
Open an [issue](https://github.com/tesserafin/tesserafin/issues) on GitHub.<br/>

<strong>Want to contribute?</strong><br/>
See the open issues on [tesserafin/tesserafin](https://github.com/tesserafin/tesserafin/issues) to find where you can help.<br/>

---

## Tesserafin Server

This repository contains the code for Tesserafin's backend server. The web client lives in the companion repository [tesserafin/tesserafin-web](https://github.com/tesserafin/tesserafin-web).

## Server Development

These instructions will help you get set up with a local development environment in order to contribute to this repository. Note that this project is supported on all major operating systems except FreeBSD, which is still incompatible.

### Prerequisites

Before the project can be built, you must first install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet) on your system.

Instructions to run this project from the command line are included here, but you will also need to install an IDE if you want to debug the server while it is running. Two options are recent versions of [Visual Studio](https://visualstudio.microsoft.com/downloads/) (at least 2022) and [Visual Studio Code](https://code.visualstudio.com/Download).

[ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) will also need to be installed. (Tesserafin uses the `jellyfin-ffmpeg` build; the dependency link is intentional.)

### Cloning the Repository

After dependencies have been installed you will need to clone a local copy of this repository. If you just want to run the server from source you can clone this repository directly, but if you are intending to contribute code changes to the project, you should set up your own fork of the repository. The following example shows how you can clone the repository directly over HTTPS.

```bash
git clone https://github.com/tesserafin/tesserafin.git
```

### Installing the Web Client

The server is configured to host the static files required for the [web client](https://github.com/tesserafin/tesserafin-web) in addition to serving the backend by default. Before you can run the server, you will need to get a copy of the web client since it is not included in this repository directly.

Note that it is recommended for development to [host the web client separately](#hosting-the-web-client-separately) from the web server with some additional configuration, in which case you can skip this step.

There are two options to get the files for the web client.

1. Build them from source following the instructions on the [tesserafin-web repository](https://github.com/tesserafin/tesserafin-web)
2. Get the pre-built files from an existing installation of the server. For example, with a Windows server installation the client files are located at `C:\Program Files\Reefin\Server\jellyfin-web`

### Running The Server

The following instructions will help you get the project up and running via the command line, or your preferred IDE.

#### Running With Visual Studio

To run the project with Visual Studio you can open the Solution (`.sln`) file and then press `F5` to run the server.

#### Running With Visual Studio Code

To run the project with Visual Studio Code you will first need to open the repository directory with Visual Studio Code using the `Open Folder...` option.

Second, you need to [install the recommended extensions for the workspace](https://code.visualstudio.com/docs/editor/extension-gallery#_recommended-extensions). Note that extension recommendations are classified as either "Workspace Recommendations" or "Other Recommendations", but only the "Workspace Recommendations" are required.

After the required extensions are installed, you can run the server by pressing `F5`.

#### Running From the Command Line

To run the server from the command line you can use the `dotnet run` command. The example below shows how to do this if you have cloned the repository into a directory named `reefin` (the default directory name) and should work on all operating systems.

```bash
cd reefin                          # Move into the repository directory
dotnet run --project Reefin.Server --webdir /absolute/path/to/jellyfin-web/dist # Run the server startup project
```

A second option is to build the project and then run the resulting executable file directly. When running the executable directly you can easily add command line options. Add the `--help` flag to list details on all the supported command line options.

1. Build the project

```bash
dotnet build                       # Build the project
cd Reefin.Server/bin/Debug/net10.0 # Change into the build output directory
```

2. Execute the build output. On Linux, Mac, etc. use `./reefin` and on Windows use `reefin.exe`.

#### Accessing the Hosted Web Client

If the Server is configured to host the Web Client, and the Server is running, the Web Client can be accessed at `http://localhost:8096` by default.

API documentation can be viewed at `http://localhost:8096/api-docs/swagger/index.html`

### Running The Tests

This repository also includes unit tests that are used to validate functionality. There are several ways to run these tests.

1. Run tests from the command line using `dotnet test`
2. Run tests in Visual Studio using the [Test Explorer](https://docs.microsoft.com/en-us/visualstudio/test/run-unit-tests-with-test-explorer)
3. Run individual tests in Visual Studio Code using the associated [CodeLens annotation](https://github.com/OmniSharp/omnisharp-vscode/wiki/How-to-run-and-debug-unit-tests)

### Advanced Configuration

The following sections describe some more advanced scenarios for running the server from source that build upon the standard instructions above.

#### Hosting The Web Client Separately

It is not necessary to host the frontend web client as part of the backend server. Hosting these two components separately may be useful for frontend developers who would prefer to host the client in a separate webpack development server for a tighter development loop. See the [tesserafin-web](https://github.com/tesserafin/tesserafin-web) repo for instructions on how to do this.

To instruct the server not to host the web content, there is a `nowebclient` configuration flag that must be set. This can be specified using the command line switch `--nowebclient` or the environment variable `REEFIN_NOWEBCONTENT=true`.

Since this is a common scenario, there is also a separate launch profile defined for Visual Studio called `Reefin.Server (nowebcontent)` that can be selected from the 'Start Debugging' dropdown in the main toolbar.

**NOTE:** The setup wizard cannot be run if the web client is hosted separately.
