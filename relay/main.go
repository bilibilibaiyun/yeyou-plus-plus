package main

// 页游++ 远程联机中继服务器
//
// 职责：主设备开房（下发 8 位数字码）→ 副设备凭码加入 → 双向透明转发字节流 → 会话关闭。
// 协议（纯文本行，'\n' 结尾）：
//   主设备：发送 "HOST\n"               -> 服务器回复 "CODE <8位数字>\n"，进入等待
//   副设备：发送 "JOIN <8位数字>\n"      -> 匹配成功回复 "LINKED\n"，开始双向转发
//   错误：  "NOTFOUND\n"（码不存在）/ "BUSY\n"（已有人加入）
// 配对成功后，主副之间的所有字节流由服务器透明转发，服务器不解析内容。

import (
	"bufio"
	"crypto/rand"
	"flag"
	"fmt"
	"io"
	"log"
	"math/big"
	"net"
	"os"
	"strings"
	"sync"
	"time"
)

const hostWaitTimeout = 5 * time.Minute // 主设备开房后等待副设备加入的最长时间

type session struct {
	code        string
	host        net.Conn
	guest       net.Conn
	hostReader  *bufio.Reader
	guestReader *bufio.Reader
	ready       chan struct{} // 副设备加入时关闭
	mu          sync.Mutex
	done        bool
}

var (
	sessions = make(map[string]*session)
	mu       sync.Mutex
)

func main() {
	port := flag.String("port", "9000", "listen port")
	flag.Parse()

	log.SetOutput(os.Stdout)
	log.SetFlags(log.LstdFlags)

	ln, err := net.Listen("tcp", ":"+*port)
	if err != nil {
		log.Fatalf("监听失败: %v", err)
	}
	log.Printf("中继服务器已启动，监听端口 %s", *port)

	for {
		conn, err := ln.Accept()
		if err != nil {
			log.Printf("accept 错误: %v", err)
			continue
		}
		go handle(conn)
	}
}

func handle(conn net.Conn) {
	remote := conn.RemoteAddr().String()
	reader := bufio.NewReader(conn)
	_ = conn.SetReadDeadline(time.Now().Add(15 * time.Second))
	line, err := reader.ReadString('\n')
	if err != nil {
		conn.Close()
		return
	}
	line = strings.TrimSpace(line)

	switch {
	case line == "HOST":
		hostFlow(conn, reader, remote)
	case strings.HasPrefix(line, "JOIN "):
		joinFlow(conn, reader, strings.TrimSpace(line[5:]), remote)
	default:
		conn.Close()
	}
}

func hostFlow(conn net.Conn, reader *bufio.Reader, remote string) {
	code := genCode()
	sess := &session{code: code, host: conn, hostReader: reader, ready: make(chan struct{})}
	mu.Lock()
	sessions[code] = sess
	mu.Unlock()

	if _, err := conn.Write([]byte("CODE " + code + "\n")); err != nil {
		closeSession(sess)
		return
	}
	log.Printf("[%s] 主设备开房，码 %s", remote, code)

	select {
	case <-sess.ready:
		_ = conn.SetReadDeadline(time.Time{})
		bridge(sess)
	case <-time.After(hostWaitTimeout):
		log.Printf("[%s] 码 %s 等待超时", remote, code)
		closeSession(sess)
	}
}

func joinFlow(conn net.Conn, reader *bufio.Reader, code string, remote string) {
	mu.Lock()
	sess := sessions[code]
	mu.Unlock()
	if sess == nil {
		conn.Write([]byte("NOTFOUND\n"))
		conn.Close()
		return
	}

	sess.mu.Lock()
	if sess.done || sess.guest != nil {
		sess.mu.Unlock()
		conn.Write([]byte("BUSY\n"))
		conn.Close()
		return
	}
	sess.guest = conn
	sess.guestReader = reader
	sess.mu.Unlock()

	if _, err := conn.Write([]byte("LINKED\n")); err != nil {
		closeSession(sess)
		return
	}
	log.Printf("[%s] 副设备加入，码 %s 配对成功", remote, code)
	close(sess.ready)
}

func bridge(sess *session) {
	host, guest := sess.host, sess.guest
	hostReader, guestReader := sess.hostReader, sess.guestReader
	if host == nil || guest == nil || hostReader == nil || guestReader == nil {
		closeSession(sess)
		return
	}
	log.Printf("码 %s 开始双向转发", sess.code)

	done := make(chan struct{}, 2)
	go func() {
		_, _ = io.Copy(guest, hostReader) // 主 -> 副
		done <- struct{}{}
	}()
	go func() {
		_, _ = io.Copy(host, guestReader) // 副 -> 主
		done <- struct{}{}
	}()
	<-done
	closeSession(sess)
	log.Printf("码 %s 会话结束", sess.code)
}

func closeSession(sess *session) {
	sess.mu.Lock()
	if sess.done {
		sess.mu.Unlock()
		return
	}
	sess.done = true
	sess.mu.Unlock()

	mu.Lock()
	if sessions[sess.code] == sess {
		delete(sessions, sess.code)
	}
	mu.Unlock()

	sess.host.Close()
	if sess.guest != nil {
		sess.guest.Close()
	}
}

// genCode 生成唯一的 8 位数字码（10000000 ~ 99999999）。
func genCode() string {
	for {
		n, err := rand.Int(rand.Reader, big.NewInt(90000000))
		if err != nil {
			code := fmt.Sprintf("%08d", time.Now().UnixNano()%100000000)
			mu.Lock()
			_, exists := sessions[code]
			mu.Unlock()
			if !exists {
				return code
			}
			continue
		}
		code := fmt.Sprintf("%08d", n.Int64()+10000000)
		mu.Lock()
		_, exists := sessions[code]
		mu.Unlock()
		if !exists {
			return code
		}
	}
}
