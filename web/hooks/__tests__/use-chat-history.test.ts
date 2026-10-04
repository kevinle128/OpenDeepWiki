/**
 * 对话历史属性测试
 * 
 * Property 2: 对话历史完整性
 * Validates: Requirements 8.1, 8.2, 8.3
 * 
 * Feature: doc-chat-assistant, Property 2: 对话历史完整性
 */
import { describe, it, expect } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import * as fc from 'fast-check'
import { 
  useChatHistory, 
  NewChatMessage,
} from '../use-chat-history'

// 生成随机工具调用
const toolCallArb = fc.record({
  id: fc.string({ minLength: 1, maxLength: 20 }),
  name: fc.string({ minLength: 1, maxLength: 50 }),
  arguments: fc.dictionary(fc.string(), fc.jsonValue()),
})

// 生成随机工具结果
const toolResultArb = fc.record({
  toolCallId: fc.string({ minLength: 1, maxLength: 20 }),
  result: fc.string(),
  isError: fc.boolean(),
})

// 生成随机消息角色
const roleArb = fc.constantFrom('user', 'assistant', 'tool') as fc.Arbitrary<'user' | 'assistant' | 'tool'>

// 生成随机新消息
const newMessageArb: fc.Arbitrary<NewChatMessage> = fc.record({
  role: roleArb,
  content: fc.string(),
  images: fc.option(fc.array(fc.base64String(), { maxLength: 3 }), { nil: undefined }),
  toolCalls: fc.option(fc.array(toolCallArb, { maxLength: 3 }), { nil: undefined }),
  toolResult: fc.option(toolResultArb, { nil: undefined }),
})

describe('useChatHistory Property Tests', () => {
  /**
   * Property 2: 对话历史完整性
   * 
   * 对于任意对话会话，消息历史必须包含所有用户消息、AI回复、工具调用和工具结果，
   * 且发送新消息时必须传递完整历史
   */
  describe('Property 2: 对话历史完整性', () => {
    it('添加的消息应该完整保留在历史记录中', () => {
      fc.assert(
        fc.property(
          fc.array(newMessageArb, { minLength: 1, maxLength: 20 }),
          (messages) => {
            const { result } = renderHook(() => useChatHistory())
            
            // 添加所有消息
            const addedIds: string[] = []
            messages.forEach(msg => {
              act(() => {
                const id = result.current.addMessage(msg)
                addedIds.push(id)
              })
            })
            
            // 验证消息数量
            expect(result.current.messages.length).toBe(messages.length)
            
            // 验证每条消息的内容完整性
            result.current.messages.forEach((storedMsg, index) => {
              const originalMsg = messages[index]
              expect(storedMsg.role).toBe(originalMsg.role)
              expect(storedMsg.content).toBe(originalMsg.content)
              expect(storedMsg.images).toEqual(originalMsg.images)
              expect(storedMsg.toolCalls).toEqual(originalMsg.toolCalls)
              expect(storedMsg.toolResult).toEqual(originalMsg.toolResult)
              expect(storedMsg.id).toBe(addedIds[index])
              expect(typeof storedMsg.timestamp).toBe('number')
            })
            
            return true
          }
        ),
        { numRuns: 100 }
      )
    })

    it('消息历史应该包含所有类型的消息（用户、助手、工具）', () => {
      fc.assert(
        fc.property(
          fc.array(roleArb, { minLength: 1, maxLength: 30 }),
          (roles) => {
            const { result } = renderHook(() => useChatHistory())
            
            // 为每个角色添加消息
            roles.forEach(role => {
              act(() => {
                result.current.addMessage({
                  role,
                  content: `Message from ${role}`,
                })
              })
            })
            
            // 验证所有角色的消息都被保留
            const storedRoles = result.current.messages.map(m => m.role)
            expect(storedRoles).toEqual(roles)
            
            return true
          }
        ),
        { numRuns: 100 }
      )
    })

    it('工具调用和工具结果应该正确关联', () => {
      fc.assert(
        fc.property(
          toolCallArb,
          toolResultArb,
          (toolCall, toolResult) => {
            const { result } = renderHook(() => useChatHistory())
            
            // 添加带工具调用的助手消息
            act(() => {
              result.current.addMessage({
                role: 'assistant',
                content: 'Using tool...',
                toolCalls: [toolCall],
              })
            })
            
            // 添加工具结果消息
            act(() => {
              result.current.addMessage({
                role: 'tool',
                content: toolResult.result,
                toolResult: { ...toolResult, toolCallId: toolCall.id },
              })
            })
            
            // 验证工具调用和结果都被保留
            expect(result.current.messages.length).toBe(2)
            expect(result.current.messages[0].toolCalls?.[0]).toEqual(toolCall)
            expect(result.current.messages[1].toolResult?.toolCallId).toBe(toolCall.id)
            
            return true
          }
        ),
        { numRuns: 100 }
      )
    })

    it('更新消息应该保持其他字段不变', () => {
      fc.assert(
        fc.property(
          newMessageArb,
          fc.string(),
          (originalMsg, newContent) => {
            const { result } = renderHook(() => useChatHistory())
            
            // 添加消息
            let msgId: string = ''
            act(() => {
              msgId = result.current.addMessage(originalMsg)
            })
            
            const originalTimestamp = result.current.messages[0].timestamp
            
            // 更新消息内容
            act(() => {
              result.current.updateMessage(msgId, { content: newContent })
            })
            
            // 验证只有content被更新，其他字段保持不变
            const updatedMsg = result.current.messages[0]
            expect(updatedMsg.content).toBe(newContent)
            expect(updatedMsg.role).toBe(originalMsg.role)
            expect(updatedMsg.images).toEqual(originalMsg.images)
            expect(updatedMsg.toolCalls).toEqual(originalMsg.toolCalls)
            expect(updatedMsg.toolResult).toEqual(originalMsg.toolResult)
            expect(updatedMsg.id).toBe(msgId)
            expect(updatedMsg.timestamp).toBe(originalTimestamp)
            
            return true
          }
        ),
        { numRuns: 100 }
      )
    })

    it('清空历史后消息列表应该为空', () => {
      fc.assert(
        fc.property(
          fc.array(newMessageArb, { minLength: 1, maxLength: 20 }),
          (messages) => {
            const { result } = renderHook(() => useChatHistory())
            
            // 添加消息
            messages.forEach(msg => {
              act(() => {
                result.current.addMessage(msg)
              })
            })
            
            expect(result.current.messages.length).toBe(messages.length)
            
            // 清空历史
            act(() => {
              result.current.clearHistory()
            })
            
            // 验证历史为空
            expect(result.current.messages.length).toBe(0)
            
            return true
          }
        ),
        { numRuns: 100 }
      )
    })

    it('每条消息应该有唯一的ID', () => {
      fc.assert(
        fc.property(
          fc.array(newMessageArb, { minLength: 2, maxLength: 50 }),
          (messages) => {
            const { result } = renderHook(() => useChatHistory())
            
            // 添加所有消息
            const ids: string[] = []
            messages.forEach(msg => {
              act(() => {
                const id = result.current.addMessage(msg)
                ids.push(id)
              })
            })
            
            // 验证所有ID都是唯一的
            const uniqueIds = new Set(ids)
            expect(uniqueIds.size).toBe(ids.length)
            
            return true
          }
        ),
        { numRuns: 100 }
      )
    })
  })
})
