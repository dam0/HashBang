﻿open System
open TypeInferred.HashBang
open ChatExample.Shared

// Here are some list extensions.
module List =
    // Returns nothing if the list is empty. 
    // Otherwise, returns the head of the list.
    let tryHead = function
        | [] -> None
        | x::_ -> Some x

// Here we define the commands that our messageBox agent can process.
type MessageBoxCommand =
    /// Sends a message to all clients and stores the message in the box.
    | Send of Message
    /// Returns all the messages since the message id provided.
    | GetSince of Guid option * AsyncReplyChannel<(string * Message)[]>

// Here we define a messageBox agent that controls the server state.
// It keeps track of messages and clients that are long-polling.
let messageBox =
    MailboxProcessor.Start(fun inbox ->
        /// Given a client's channel and the last message identifier
        /// it saw, this function returns all messages after that message.
        let notify msgs (lastId, channel : AsyncReplyChannel<_>) =
            msgs |> Seq.takeWhile (fun (id, _) -> 
                match lastId with
                | None -> true
                | Some lastId -> id <> lastId)
            |> Seq.map (fun (id, msg) -> id.ToString(), msg)
            |> Seq.toArray |> channel.Reply
        /// Loops forever. Holds the messages seen so far and the currently
        /// waiting client channels in its closure.
        let rec loop msgs waiting =
            async {
                // Asynchronously waits for the next command.
                let! command = inbox.Receive()
                match command with
                // Processes a request from a client for new messages.
                | GetSince(lastId, channel) ->
                    match List.tryHead msgs with
                    // If there are new messages since the client last saw anything
                    // we immediately respond with those new messages.
                    | Some(id, _) when Some id <> lastId ->
                        notify msgs (lastId, channel)
                        return! loop msgs waiting
                    // Otherwise, we wait for something to happen.
                    | Some _ | None ->
                        return! loop msgs ((lastId, channel) :: waiting)
                // Processes a request from a client to distribute a new message
                | Send msg ->
                    // Assigns the message an identifier.
                    let id = Guid.NewGuid()
                    let newMsgs = (id, msg) :: msgs
                    // Notifies all waiting clients.
                    waiting |> List.iter (notify newMsgs)
                    return! loop newMsgs []
            }
        loop [] []
    )

let cometTimeout = 10000

// Here we define any route patterns that the server can handle.
type Routes = RoutesProvider< "
POST    /messages                          # Messages.Send   # Broadcasts a new message
GET     /messages?since=string_option      # Messages.Since  # Returns all the messages since the given id
">

/// Creates a Chat API.
let createServer() =
    // Here we define what the server will listen to.
    Website.At "http://*:8080/"
    // Here we bind the route patterns to handlers.
    |> Website.WithRouteHandlers 
        [
            Routes.Messages.Send.CreateHandler Json Json (fun ((), message) ps -> async {
                messageBox.Post(Send message)
                return OK message
            })

            Routes.Messages.Since.CreateHandler Json (fun () ps -> async {
                let lastId =
                    match ps.since with
                    | None -> None
                    | Some id -> Some(Guid.Parse id)
                let! msgs = messageBox.PostAndTryAsyncReply((fun c -> 
                                GetSince(lastId, c)), cometTimeout)
                return OK msgs
            })
        ] 
    // Here we specify that the API should publish metadata.
    // This allows us to hook into the API in a type safe way from the
    // HashBangAPI type provider in .NET and FunScript/JS clients. 
    |> Website.WithMetadata
    // This runs the server.
    |> Website.Start
    
[<EntryPoint>]
let main argv = 
    printfn "%A" argv
    System.Net.Netsh.addUrlAcl "http://*:8080/"
    use webserver = createServer()
    printfn "Press Return key to kill server."
    System.Console.ReadLine() |> ignore
    0 // return an integer exit code