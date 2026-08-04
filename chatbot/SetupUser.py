import os
import requests
from dotenv import load_dotenv
import json

load_dotenv(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".env"))

chatApiURL = f"https://chat.botpress.cloud/{os.getenv('botpressChatApi')}"

conversationID = "conv_01KZ5GBN8NCK8AZFC7WYSBWNJ9"

class Botpress:
    def __init__(self):
        self.headers = {
            "accept": "application/json",
            "Content-Type": "application/json",
            "x-user-key": os.getenv("userKey")
        }

        self.chatOfUserAndBotMessage = []

    
    def _requests(self, method, path, json=None):
        URL = f"{chatApiURL}{path}"
        try:
            respond = requests.request(method, URL, headers=self.headers, json=json)
            respond.raise_for_status()
            return respond.json()

        except requests.HTTPError:
            return respond.status_code, respond.text

    def createUser(self, name, id):
        userid = {"name": name, "id": id}
        return self._requests("POST", "/users", json=userid)

    def createConversation(self):
        return self._requests("POST", "/conversations", json={})

    def createMessage(self, conversationID, message):
        body = {
            "payload": {"type": "text", "text": message},
            "conversationId": conversationID,
        }
        return self._requests("POST", "/messages", json=body)

    def getMessage(self, conversationId):
        return self._requests("GET", f"/conversations/{conversationId}/messages")


    def getLastBotMessage(self):
        messages = self.getMessage(conversationID)
        if not isinstance(messages, dict) or "messages" not in messages:
            return None
        for msg in messages["messages"]:
            if msg["userId"] != "EveryoneShareSameID":
                return msg["payload"].get("text")
        return None

    def safeMessageToJsonFile(self):
        messages = self.getMessage(conversationID)
        if not isinstance(messages, dict) or "messages" not in messages:
            return
        reversedDict = messages["messages"][::-1]

        result = []
        for i in range(0, len(reversedDict) - 1, 2):
            a, b = reversedDict[i], reversedDict[i + 1]
            if "EveryoneShareSameID" == a["userId"]:
                userMsg, botMsg = a, b
            else:
                userMsg, botMsg = b, a
            result.append({"user": userMsg["payload"]["text"], "bot": botMsg["payload"]["text"]})

        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ChatbotMessages.json'), 'w', encoding='utf-8') as json_file:
            json.dump(result, json_file, ensure_ascii=False, indent=4)

