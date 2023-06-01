import getopt
import sys

from os import linesep
from simple_websocket_server import WebSocketServer, WebSocket


class WebRTCServer(WebSocket):

    def handle(self):
        if self.data is None:
            self.data = 'N/A'

        client_id = f"{self.address[0]}:{self.address[1]}"

        print(f"Message received from {client_id}, saying: {self.data}")
        print(f"{linesep}")

        for client_id in [x for x in clients if x != client_id]:
            clients[client_id].send_message(self.data)

    def connected(self):
        client_id = f"{self.address[0]}:{self.address[1]}"
        clients[client_id] = self
        print(f"{client_id} connected")

    def handle_close(self):
        client_id = f"{self.address[0]}:{self.address[1]}"
        clients.pop(client_id, None)
        print(f"{client_id} disconnected")


def run_server(address, port):
    clients = {}
    server = WebSocketServer(address, port, WebRTCServer)
    server.serve_forever()


def main(argv):
    address = '145.90.222.224'
    port = 8000
    opts, args = getopt.getopt(argv,"ha:p:",["address=","port="])
    for opt, arg in opts:
        if opt == '-h':
            print ('WSServer.py -a <address> -p <port>')
            sys.exit()
        elif opt in ("-a", "--address"):
            address = arg
        elif opt in ("-p", "--port"):
            port = int(arg)
    print(f"Setting up server at {address}:{port}")
    run_server(address, port)


if __name__ == '__main__':
    main(sys.argv[1:])
