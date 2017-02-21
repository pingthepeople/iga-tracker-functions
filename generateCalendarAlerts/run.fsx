// Configure Database 

#r "System.Data"
#r "../packages/Dapper/lib/net45/Dapper.dll"
#r "../packages/Newtonsoft.Json/lib/net45/Newtonsoft.Json.dll"

#load "../shared/model.fs"
#load "../shared/queries.fs"
#load "../shared/http.fsx"
#load "../shared/db.fsx"

open System
open System.Data.SqlClient
open System.Dynamic
open System.Collections.Generic
open Dapper
open IgaTracker.Model
open IgaTracker.Queries
open IgaTracker.Http
open IgaTracker.Db
open Newtonsoft.Json

// Find the user for whom we're generating a particular alert.
let locateUserToAlert (users:User seq) userBill =
    users |> Seq.find (fun u -> u.Id = userBill.UserId)

// Format a nice description of the action
let prettyPrint sa =
    match sa.ActionType with
    | ActionType.CommitteeReading -> sprintf "is scheduled for a committe reading on %s between %s and %s in %s" (sa.Date.ToString("MM/dd/yyyy")) sa.Start sa.End sa.Location
    | ActionType.SecondReading -> sprintf "is scheduled for a second reading in the %s on %s" sa.Location (sa.Date.ToString("MM/dd/yyyy"))
    | ActionType.ThirdReading -> sprintf "is scheduled for a third reading in the %s on %s" sa.Location (sa.Date.ToString("MM/dd/yyyy"))
    | _ -> "(some other event type?)"

// Format a nice message body
let body (bill:Bill) (scheduledAction:ScheduledAction) =
    sprintf "%s ('%s') %s. (@ %s)" bill.Name (bill.Title.TrimEnd('.')) (prettyPrint scheduledAction) (scheduledAction.Date.ToString())
// Format a nice message subject
let subject (bill:Bill) =
    sprintf "Update on %s" bill.Name

// Generate email message models
let generateEmailMessages (bill:Bill) action users userBills =
    userBills 
    |> Seq.map (fun ub -> 
        locateUserToAlert users ub 
        |> (fun u -> {MessageType=MessageType.Email; Recipient=u.Email; Subject=(subject bill); Body=(body bill action)}))
// Generate SMS message models
let generateSmsMessages (bill:Bill) action users userBills = 
    userBills 
    |> Seq.map (fun ub -> 
        locateUserToAlert users ub 
        |> (fun u -> {MessageType=MessageType.SMS; Recipient=u.Mobile; Subject=(subject bill); Body=(body bill action)}))

// Fetch user/bill/action/ records from database to support message generation
let fetchUserBills (cn:SqlConnection) id =
    let action = cn |> dapperParametrizedQuery<ScheduledAction> "SELECT * FROM ScheduledAction WHERE Id = @Id" {Id=id} |> Seq.head
    let bill = cn |> dapperParametrizedQuery<Bill> "SELECT * FROM Bill WHERE Id = @Id" {Id=action.BillId} |> Seq.head
    let userBills = cn |> dapperParametrizedQuery<UserBill> "SELECT * FROM UserBill WHERE BillId = @Id" {Id=action.BillId}
    let userIds = userBills |> Seq.map (fun ub -> ub.UserId)
    let users = cn |> dapperParametrizedQuery<User> "SELECT * FROM [User] WHERE Id IN @Ids" {Ids=userIds}
    (bill, action, users, userBills)

// Create action alert messages for people that have opted-in to receiving them
let generateAlerts (cn:SqlConnection) id =
    let (bill, action, users, userBills) = id |> fetchUserBills cn
    let emailMessages = userBills |> Seq.filter(fun ub -> ub.ReceiveAlertEmail) |> generateEmailMessages bill action users
    let smsMessages = userBills |> Seq.filter(fun ub -> ub.ReceiveAlertSms) |>  generateSmsMessages bill action users
    (emailMessages, smsMessages)

#r "../packages/Microsoft.Azure.WebJobs/lib/net45/Microsoft.Azure.WebJobs.Host.dll"

open Microsoft.Azure.WebJobs.Host

let Run(scheduledActionId: string, notifications: ICollector<string>, log: TraceWriter) =
    log.Info(sprintf "F# function 'generateScheduledActionAlerts' executed for action %s at %s" scheduledActionId (DateTime.Now.ToString()))
    let cn = new SqlConnection(System.Environment.GetEnvironmentVariable("SqlServer.ConnectionString"))

    log.Info(sprintf "[%s] Generating scheduled action alerts ..." (DateTime.Now.ToString("HH:mm:ss.fff")))
    let (emailMessages, smsMessages) = (Int32.Parse(scheduledActionId)) |> generateAlerts cn
    log.Info(sprintf "[%s] Generating scheduled action alerts [OK]" (DateTime.Now.ToString("HH:mm:ss.fff")))

    log.Info(sprintf "[%s] Enqueueing scheduled action alerts ..." (DateTime.Now.ToString("HH:mm:ss.fff")))
    emailMessages |> Seq.iter (fun m -> 
        log.Info(sprintf "[%s]   Enqueuing email alert to '%s' re: '%s'" (DateTime.Now.ToString("HH:mm:ss.fff")) m.Recipient m.Subject )
        notifications.Add(JsonConvert.SerializeObject(m)))
    smsMessages |> Seq.iter (fun m -> 
        log.Info(sprintf "[%s]   Enqueuing SMS alert to '%s' re: '%s'" (DateTime.Now.ToString("HH:mm:ss.fff")) m.Recipient m.Subject)
        notifications.Add(JsonConvert.SerializeObject(m)))
    log.Info(sprintf "[%s] Enqueueing scheduled action alerts [OK]" (DateTime.Now.ToString("HH:mm:ss.fff")))
    