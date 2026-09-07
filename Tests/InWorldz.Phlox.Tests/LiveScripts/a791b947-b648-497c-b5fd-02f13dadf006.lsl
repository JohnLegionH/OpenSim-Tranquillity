// =====================================================================================================
// Box Unpacker V  (Turbo+) 2.5 by Vegro Solari
// =====================================================================================================
// Easy to use UNPACK script with Terms of Service function for vendors. 
// Unlike other scripts, it works for all item types, including NO-COPY ones such as fairies, 
// kittens etc. Small "gotcha" applying only for NO-COPY items: there is no way to get them to be sent
// to a folder, like the script does with COPY items. NO-COPY items will appear in the user's "Objects"
// folder like normal, and work perfectly other than that.
// 
// ***How to use***
//  - Put your items into your container
//  - Add the script
//  - Add a full perm notecard called "TOS" if you want users to accept your terms before they can unpack
// Done!
// ======================================================================================================

// OPTIONAL USER-EDITABLE SETTINGS: 

// What your TOS / Policy notecard will be called (so the script knows)
string TOS = "***PLEASE READ: USER GUIDE***";

// The message dialog that users will see when they click your box
string YourMessage =  "You must read the USER GUIDE to unpack this product. Do you want to continue?";

//  The name of the folder that will be created for your items when given to users
//  If you leave it  as is, then it will automatically use your box prim's name for this
string nameOfFolderToBeCreated = "[Mobius] Gen6 Mobian Male Hedgehog Kit";  

// Want to add a welcome message or tips for users when they first rez your box? Edit this. 
string messageToSendUponRez = "";

// You could change this if you want only certain types of items to be given to users, not all the contents of the box
//  Example: giving only images, or only sounds, or only objects. By default it will collect and give everything.
integer typeOfInventoryItemsToBeSent = INVENTORY_ALL;

// Should the upacker script self-destruct after it's done its job? Set to TRUE if that's what you need.
integer killAfterCompletion = TRUE;

// ===============================================================================================
//  Copyright Notice:
// ===============================================================================================
//  This script is FREE SOFTWARE. You're free to do whatever you want with it.
//  If you try to sell it for millions to scam others then you suck, man.
//  by Vegro Solari in 2016
// ===============================================================================================

//Internal variables  (normally not to be changed by the user)
key creator; key owner; 
integer gListener;  // for dialog   
integer gMenuChan;  // automatically chosen channel for the dialog menu
integer addThisScriptToFolder = FALSE;
integer AcceptTermsBeforeCanUnpack = TRUE;

init()
{
//  do not use an empty string
    if(nameOfFolderToBeCreated == "Folder") nameOfFolderToBeCreated = llGetObjectName();
 
//  leave empty to not send a message upon rez
    messageToSendUponRez = "";
 
    gMenuChan = random_integer(99999,2147483647);
    
    llSetTouchText("Unpack");
    
    owner = llGetOwner();
}
 
// Tricky code which returns random integers between min and max, both inclusive.
// Careful: fails to work correctly if min > max
integer random_integer( integer min, integer max )
{
  return min + (integer)( llFrand( max - min + 1 ) );
}

// =======================
// Main unpacking function
// =======================
unpackDirect()
{
        key owner = llGetOwner();
        string thisScript = llGetScriptName();
        integer messageOnce = 0;
        
        list inventoryItems;
        integer inventoryNumber = llGetInventoryNumber(typeOfInventoryItemsToBeSent);
 
        integer index=0;
        for ( ; index < inventoryNumber; ++index )
        {
            string itemName = llGetInventoryName(typeOfInventoryItemsToBeSent, index);
            if (itemName != llGetScriptName() && itemName != "" )
            {
                if (llGetInventoryPermMask(itemName, MASK_OWNER) & PERM_COPY)
                {
                   inventoryItems += itemName;
                }
                else
                {
                    llGiveInventory(owner, itemName);    // 2.0 seconds delay
                    index--; // one item (the NO COPY one) already handed out
                        if(!messageOnce) 
                        {
                            llInstantMessage(owner,
                         "/me [" + thisScript + "]: NO-COPY Items have been sent into your Objects folder '"
                            + nameOfFolderToBeCreated + "' within your inventory.");
                            messageOnce = 1;
                        }
                }
            }
        }
       
        if (inventoryItems != [])
        {
           llGiveInventoryList(owner, llGetObjectName(), inventoryItems);    // 3.0 seconds delay
           llInstantMessage(owner,
        "/me [" + thisScript + "]: Items have been sent into a folder named '"
        + nameOfFolderToBeCreated + "' within your inventory.");
        }
        
         // end of inventory sending code

        if (killAfterCompletion)
              llRemoveInventory(llGetScriptName());
}

 
default
{
    state_entry()
    {
       init();

       if (messageToSendUponRez != "")
            llInstantMessage(owner, messageToSendUponRez);
      
    }
    on_rez(integer start_param)
    {
        llResetScript();
    }
 
    touch_start(integer num_detected)
    {
        owner = llGetOwner();
        creator = llGetCreator();
        key id = llDetectedKey(0);
 
    //  if not the owner touching, abort process
        if (id != owner)
            return;
     
        // Dialog / TOS section
        // Kill off any outstanding listener, to avoid any chance of multiple listeners being active
        llListenRemove(gListener);
        
        // If a TOS notecard has been supplied,  use the TOS accept/decline menu
        if(llGetInventoryType(TOS) != INVENTORY_NOTECARD) AcceptTermsBeforeCanUnpack = FALSE;
        else AcceptTermsBeforeCanUnpack = TRUE;
        
        if(AcceptTermsBeforeCanUnpack)
        {
            // Listen to any reply from that user only, and only on the same channel to be used by llDialog
            // It's best to set up the listener before issuing the dialog
            gListener = llListen(-gMenuChan, "", id, "");
            // Send a dialog to that person. We'll use a fixed negative channel number for simplicity
            llDialog(id, "\n"+YourMessage, ["I agree", "Disagree" ] , -gMenuChan);
            // Start a  few minute timer, after which we will stop listening for responses
            llSetTimerEvent(600.0);    
            
            // If there was a TOS notecard included, give it to the user
            if(llGetInventoryType(TOS) == INVENTORY_NOTECARD)
            {
               llGiveInventory(owner,TOS);
            }
        }
        else { unpackDirect(); } 
    }
    
   
 
    //----------------------------------------------------------------------------------------------------
    
    listen(integer chan, string name, key id, string msg)
    {
        key owner = llGetOwner();
        string thisScript = llGetScriptName();
        
        if (msg == "I agree")
        {
           unpackDirect();
        }
        else
        {
             llInstantMessage(owner,
            "/me [" + thisScript + "]: Sorry, you must agree to the TOS to unpack this product!");
            // The user did not click "Yes" ...
            // Make the timer fire immediately, to do clean-up actions
            llSetTimerEvent(0.1);    
        }
    
    }
    
    timer()
    {
        // Stop listening. It's wise to do this to reduce lag
        llListenRemove(gListener);
        // Stop the timer now that its job is done
        llSetTimerEvent(0);
    }
}

 
