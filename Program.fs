open System
open System.IO
open System.Net
open System.Diagnostics
open System.Text
open System.Text.Json
open System.Net.Http

// Типы для конфигурации и данных аккаунта
type LauncherConfig = {
    MinecraftDir: string
    JavaPath: string
    DefaultMemoryMB: int
    ClientId: string
    AuthRedirectUrl: string
}

type MinecraftAccount = {
    Username: string
    AccessToken: string
    RefreshToken: string
    Uuid: string
}

type ModLoaderType =
    | Forge
    | Fabric
    | Vanilla

type MinecraftVersion = {
    Id: string
    Type: string
    Url: string
    ModLoader: ModLoaderType option
}

// Модуль для работы с Microsoft OAuth
module MicrosoftAuth =
    let private httpClient = new HttpClient()

    let getAuthCodeUrl config =
        sprintf "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?client_id=%s&response_type=code&redirect_uri=%s&response_mode=query&scope=XboxLive.signin%%20offline_access"
            config.ClientId config.AuthRedirectUrl

    let exchangeCodeForToken config authCode = async {
        let content = new FormUrlEncodedContent([
            KeyValuePair.Create("client_id", config.ClientId)
            KeyValuePair.Create("code", authCode)
            KeyValuePair.Create("redirect_uri", config.AuthRedirectUrl)
            KeyValuePair.Create("grant_type", "authorization_code")
        ])

        let! response = httpClient.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", content) |> Async.AwaitTask
        let! responseContent = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        return JsonDocument.Parse(responseContent).RootElement
    }

    let authenticateWithXbox token = async {
#
        return {| Token = "xbox_token"; Uhs = "user_hash" |}
    }

    let authenticateWithMinecraft xboxToken = async {

        return {| AccessToken = "mc_access_token"; Username = "MinecraftPlayer"; Uuid = Guid.NewGuid().ToString() |}
    }


module ModLoaderInstaller =
    let private downloadFileAsync (url: string) (path: string) = async {
        use client = new WebClient()
        do! client.DownloadFileTaskAsync(Uri(url), path) |> Async.AwaitTask
    }

    let installForge version mcDir = async {
        let forgeUrl = sprintf "https://files.minecraftforge.net/maven/net/minecraftforge/forge/%1$s-%2$s/forge-%1$s-%2$s-installer.jar" version.Id "recommended"
        let installerPath = Path.Combine(mcDir, "forge-installer.jar")
        
        printfn "Download Forge installer..."
        do! downloadFileAsync forgeUrl installerPath
        
        printfn "Install Forge..."
        let psi = ProcessStartInfo(
            FileName = "java",
            Arguments = sprintf "-jar \"%s\" --installServer" installerPath,
            WorkingDirectory = mcDir,
            UseShellExecute = false
        )
        
        let p = Process.Start(psi)
        p.WaitForExit()
        File.Delete(installerPath)
        
        return { version with ModLoader = Some Forge }
    }

    let installFabric version mcDir = async {
        let fabricUrl = "https://maven.fabricmc.net/net/fabricmc/fabric-installer/latest/fabric-installer.jar"
        let installerPath = Path.Combine(mcDir, "fabric-installer.jar")
        
        printfn "Download Fabric installer..."
        do! downloadFileAsync fabricUrl installerPath
        
        printfn "Instaling Fabric..."
        let psi = ProcessStartInfo(
            FileName = "java",
            Arguments = sprintf "-jar \"%s\" client -mcversion %s" installerPath version.Id,
            WorkingDirectory = mcDir,
            UseShellExecute = false
        )
        
        let p = Process.Start(psi)
        p.WaitForExit()
        File.Delete(installerPath)
        
        return { version with ModLoader = Some Fabric }
    }


module MinecraftLauncher =
    let defaultConfig = {
        MinecraftDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft")
        JavaPath = "java"
        DefaultMemoryMB = 4096
        ClientId = "YOUR_CLIENT_ID" // Зарегистрируйте приложение в Azure Portal
        AuthRedirectUrl = "http://localhost:8080/auth-response"
    }

    let getAvailableVersions() = async {
        use client = new HttpClient()
        let! response = client.GetStringAsync("https://launchermeta.mojang.com/mc/game/version_manifest.json") |> Async.AwaitTask
        let doc = JsonDocument.Parse(response)
        
        return doc.RootElement.GetProperty("versions").EnumerateArray()
                |> Seq.map (fun v -> {
                    Id = v.GetProperty("id").GetString()
                    Type = v.GetProperty("type").GetString()
                    Url = v.GetProperty("url").GetString()
                    ModLoader = None
                })
                |> Seq.toList
    }

    let authenticateAccount config = async {
        printfn "Opening browser\"
        let authUrl = MicrosoftAuth.getAuthCodeUrl config
        Process.Start(new ProcessStartInfo(FileName = authUrl, UseShellExecute = true)) |> ignore
        
        printfn "После аутентификации введите полученный код: "
        let authCode = Console.ReadLine()
        
        let! tokenResponse = MicrosoftAuth.exchangeCodeForToken config authCode
        let accessToken = tokenResponse.GetProperty("access_token").GetString()
        let refreshToken = tokenResponse.GetProperty("refresh_token").GetString()
        
        let! xboxAuth = MicrosoftAuth.authenticateWithXbox accessToken
        let! mcAuth = MicrosoftAuth.authenticateWithMinecraft xboxAuth.Token
        
        return {
            Username = mcAuth.Username
            AccessToken = mcAuth.AccessToken
            RefreshToken = refreshToken
            Uuid = mcAuth.Uuid
        }
    }

    let launchMinecraft config (account: MinecraftAccount) (version: MinecraftVersion) =
        let javaArgs = sprintf "-Xmx%iM -Xms%iM" config.DefaultMemoryMB (config.DefaultMemoryMB / 2)
        let jarName = 
            match version.ModLoader with
            | Some Forge -> sprintf "%s-forge-%s" version.Id "recommended"
            | Some Fabric -> sprintf "fabric-loader-%s-%s" "0.14.9" version.Id
            | None -> version.Id
        
        let jarPath = Path.Combine(config.MinecraftDir, "versions", jarName, sprintf "%s.jar" jarName)
        let nativesPath = Path.Combine(config.MinecraftDir, "versions", jarName, "natives")
        
        let args = 
            match version.ModLoader with
            | Some Forge | Some Fabric ->
                sprintf "%s -Djava.library.path=\"%s\" -cp \"%s\" net.minecraft.client.main.Main --username %s --version %s --gameDir \"%s\" --assetsDir \"%s\" --accessToken %s --uuid %s --userType mojang"
                    javaArgs nativesPath jarPath account.Username jarName config.MinecraftDir (Path.Combine(config.MinecraftDir, "assets")) account.AccessToken account.Uuid
            | None ->
                sprintf "%s -Djava.library.path=\"%s\" -cp \"%s\" net.minecraft.client.main.Main --username %s --version %s --gameDir \"%s\" --assetsDir \"%s\" --accessToken %s --uuid %s --userType mojang"
                    javaArgs nativesPath jarPath account.Username version.Id config.MinecraftDir (Path.Combine(config.MinecraftDir, "assets")) account.AccessToken account.Uuid
        
        let startInfo = ProcessStartInfo(
            FileName = config.JavaPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

        let process = new Process(StartInfo = startInfo)
        process.OutputDataReceived.Add(fun args -> Console.WriteLine(args.Data))
        process.ErrorDataReceived.Add(fun args -> Console.WriteLine(args.Data))

        printfn "Запускаем Minecraft %s для пользователя %s..." version.Id account.Username
        process.Start() |> ignore
        process.BeginOutputReadLine()
        process.BeginErrorReadLine()
        process.WaitForExit()

// Основная программа
[<EntryPoint>]
let main argv = async {
    printfn "=== Minecraft Launcher на F# ==="
    
    let config = MinecraftLauncher.defaultConfig
    
    // Аутентификация
    let! account = MinecraftLauncher.authenticateAccount config
    printfn "Аутентифицирован как: %s" account.Username
    
    // Выбор версии
    let! versions = MinecraftLauncher.getAvailableVersions()
    printfn "\nДоступные версии:"
    versions |> List.iteri (fun i v -> printfn "%d. %s (%s)" (i+1) v.Id v.Type)
    
    printf "\nВыберите версию (1-%d): " versions.Length
    let versionIndex = Console.ReadLine() |> int
    let selectedVersion = versions.[versionIndex - 1]
    
    // Выбор модлоадера
    printfn "\nВыберите модлоадер:"
    printfn "1. Vanilla (без модов)"
    printfn "2. Forge"
    printfn "3. Fabric"
    printf "Ваш выбор (1-3): "
    let loaderChoice = Console.ReadLine() |> int
    
    let! versionWithLoader = 
        match loaderChoice with
        | 1 -> async { return { selectedVersion with ModLoader = None } }
        | 2 -> ModLoaderInstaller.installForge selectedVersion config.MinecraftDir
        | 3 -> ModLoaderInstaller.installFabric selectedVersion config.MinecraftDir
        | _ -> async { return selectedVersion }
    
    // Запуск игры
    MinecraftLauncher.launchMinecraft config account versionWithLoader
    
    return 0
}
|> Async.RunSynchronously